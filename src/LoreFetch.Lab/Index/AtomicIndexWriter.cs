using LoreFetch.Core.Identification;

namespace LoreFetch.Lab.Index;

/// Writes `cards.lfidx` atomically: to a temp file in the SAME directory as
/// the real destination (never the OS temp dir -- DECISIONS.md's storage trap
/// about `%TEMP%` silently degrading a same-volume rename to copy+delete
/// applies here just as much as it does to the collection CSV), verifies
/// what actually landed on disk, and only then `File.Move(overwrite: true)`
/// into place.
///
/// That ordering is the whole point: if the write throws partway through --
/// a bad entry, a disk error, anything -- the half-written bytes exist only
/// in the temp file. `--out` is never opened for writing directly, so it is
/// either untouched (first build) or still holds its previous, complete
/// contents (a rebuild) right up until the single atomic rename at the end.
/// A failed build can therefore never leave `--out` truncated or corrupt.
public static class AtomicIndexWriter
{
    /// `write` defaults to `HashIndexFile.Write` and should be left at that
    /// default in production -- it is a parameter (rather than this type
    /// calling `HashIndexFile.Write` directly) purely so
    /// `AtomicIndexWriterTests` can chaos-test the "a failure leaves no
    /// partial --out" rule with a stub that writes garbage and then throws,
    /// without needing to fabricate a real disk-full or corrupt-entry
    /// condition against the real writer.
    public static void WriteAndPublish(
        string outPath,
        IReadOnlyList<HashIndexEntry> entries,
        Action<string, IReadOnlyList<HashIndexEntry>>? write = null)
    {
        ArgumentNullException.ThrowIfNull(outPath);
        ArgumentNullException.ThrowIfNull(entries);
        write ??= HashIndexFile.Write;

        var fullOutPath = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            $".{Path.GetFileName(fullOutPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            write(tempPath, entries);

            // Re-read what was ACTUALLY written, while it is still only a
            // temp file, so a corrupt write is caught before it is ever
            // exposed as `--out` rather than after.
            var verify = HashIndexFile.Read(tempPath);
            if (verify.Entries.Count != entries.Count)
            {
                throw new InvalidOperationException(
                    $"Index verification failed: wrote {entries.Count} entries but read back {verify.Entries.Count}.");
            }

            var expectedOracleCount = entries.Select(e => e.OracleId).Distinct(StringComparer.Ordinal).Count();
            if (verify.OracleTable.Count != expectedOracleCount)
            {
                throw new InvalidOperationException(
                    $"Index verification failed: expected {expectedOracleCount} oracle cards but read back " +
                    $"{verify.OracleTable.Count}.");
            }

            File.Move(tempPath, fullOutPath, overwrite: true);
        }
        finally
        {
            // Whether we got here via success (temp already moved away, so
            // this is a no-op) or via a thrown exception (temp is broken
            // and must never be mistaken for a real index later), the temp
            // file never survives this call.
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
