using System.Diagnostics;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Images;
using OpenCvSharp;

namespace LoreFetch.Lab.Index;

public sealed class IndexBuildOptions
{
    public required string CacheDir { get; init; }

    /// Default: processor count, same default `Parallel.For` itself would
    /// pick if left unset -- named explicitly here only so `--parallelism`
    /// and the determinism tests can override it.
    public int Parallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// 0 disables progress lines entirely -- used by tests, which run
    /// small enough builds that progress output would just be noise.
    public int ProgressInterval { get; init; } = 2000;
}

/// One completed build: the hashed entries (already in manifest order --
/// see `IndexBuilder`'s own doc comment for why), which arts could not be
/// hashed at all, and how many decoded arts were not the expected 488x680.
public sealed record IndexBuildResult(
    IReadOnlyList<HashIndexEntry> Entries,
    IReadOnlyList<string> MissingArtworkIds,
    int UnexpectedSizeCount,
    TimeSpan Elapsed);

/// Runs a manifest through the SAME two Core functions the rest of the
/// codebase is pinned to -- `ReferenceTransform.Prepare` then
/// `CardHasher.Hash` -- and nothing else. This is deliberate: `build-index`
/// COMPOSES Core's functions, it does not reimplement any step of them.
/// See CLAUDE.md "The one gate that matters most" -- a third transform
/// living here, even one that happens to agree with the other two today,
/// is exactly the silent divergence that gate exists to catch. There is no
/// other code in this type that touches pixels.
///
/// Parallelised over manifest entries (`IndexBuildOptions.Parallelism`,
/// default = processor count), but each entry writes its own outcome into
/// a slot at ITS OWN manifest index -- never appended to a shared list in
/// whatever order threads happen to finish -- so `Entries` comes back in
/// manifest order regardless of parallelism or scheduling. Two builds of
/// the same manifest and cache, at any parallelism, therefore produce
/// byte-identical index files; `IndexBuilderTests` asserts this directly
/// rather than leaving it as an implied property of the indexed-slot
/// design.
public static class IndexBuilder
{
    public static IndexBuildResult Build(IReadOnlyList<ManifestEntry> entries, IndexBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = Stopwatch.StartNew();

        var slots = new HashIndexEntry?[entries.Count];
        var missing = new bool[entries.Count];
        var unexpectedSize = new bool[entries.Count];
        var completed = 0;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) };

        Parallel.For(0, entries.Count, parallelOptions, i =>
        {
            HashOne(entries[i], options.CacheDir, slots, missing, unexpectedSize, i);

            var completedNow = Interlocked.Increment(ref completed);
            if (options.ProgressInterval > 0 && completedNow % options.ProgressInterval == 0)
            {
                Console.WriteLine($"build-index progress: {completedNow}/{entries.Count}");
            }
        });

        var resultEntries = new List<HashIndexEntry>(entries.Count);
        var missingIds = new List<string>();
        var unexpectedCount = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            if (missing[i])
            {
                missingIds.Add(entries[i].ArtworkId);
                continue;
            }

            resultEntries.Add(slots[i]!.Value);
            if (unexpectedSize[i])
            {
                unexpectedCount++;
            }
        }

        stopwatch.Stop();
        return new IndexBuildResult(resultEntries, missingIds, unexpectedCount, stopwatch.Elapsed);
    }

    /// One entry's outcome, written into `slots[i]`/`missing[i]`/
    /// `unexpectedSize[i]` -- never returned or appended anywhere -- so
    /// concurrent calls from `Parallel.For` never contend on a shared
    /// collection; each call only ever touches index `i` of each array.
    ///
    /// Only the file-open/decode step is inside the try/catch: a corrupt
    /// or truncated JPEG is "undecodable" and folds into the same bucket
    /// as "missing" (per B4c's "do NOT silently drop it" rule). A failure
    /// inside `ReferenceTransform.Prepare` or `CardHasher.Hash` themselves
    /// is NOT caught here -- that would be a real bug in the shared
    /// transform, and swallowing it into "missing image" would hide
    /// exactly the kind of defect CLAUDE.md's gate exists to surface. It
    /// propagates out of `Parallel.For` as an `AggregateException`
    /// instead, which is the correct, loud failure for that case.
    private static void HashOne(
        ManifestEntry entry,
        string cacheDir,
        HashIndexEntry?[] slots,
        bool[] missing,
        bool[] unexpectedSize,
        int i)
    {
        var path = ImageCache.GetImagePath(cacheDir, entry.ArtworkId);
        if (!File.Exists(path))
        {
            missing[i] = true;
            return;
        }

        Mat source;
        try
        {
            source = Cv2.ImRead(path, ImreadModes.Color);
        }
        catch (Exception)
        {
            missing[i] = true;
            return;
        }

        using (source)
        {
            if (source.Empty())
            {
                missing[i] = true;
                return;
            }

            if (source.Width != RectifiedCard.CanonicalWidth || source.Height != RectifiedCard.CanonicalHeight)
            {
                unexpectedSize[i] = true;
            }

            using var gray = ReferenceTransform.Prepare(source);
            var hash = CardHasher.Hash(gray);
            slots[i] = new HashIndexEntry(hash, entry.OracleId, entry.OracleName, entry.ArtworkId, entry.IsBasicLand);
        }
    }
}
