using LoreFetch.Core.Identification;
using LoreFetch.Lab.Index;
using Xunit;

namespace LoreFetch.Tests.Lab.Index;

public sealed class AtomicIndexWriterTests : IDisposable
{
    private readonly string _dir = IndexBuildFixtures.NewTempCacheDir();

    public void Dispose() => IndexBuildFixtures.DeleteQuietly(_dir);

    private static HashIndexEntry MakeEntry(string artworkId) => new(
        Hash: new LoreFetch.Core.Identification.CardHash(new ulong[LoreFetch.Core.Identification.CardHash.WordCount]),
        OracleId: "oracle-" + artworkId,
        OracleName: "Name " + artworkId,
        ArtworkId: artworkId,
        IsBasicLand: false);

    [Fact]
    public void WriteAndPublish_Succeeds_OutFileIsReadableAndCorrect()
    {
        var outPath = Path.Combine(_dir, "cards.lfidx");
        var entries = new[] { MakeEntry("a"), MakeEntry("b") };

        AtomicIndexWriter.WriteAndPublish(outPath, entries);

        Assert.True(File.Exists(outPath));
        var data = HashIndexFile.Read(outPath);
        Assert.Equal(2, data.Entries.Count);
    }

    [Fact]
    public void WriteAndPublish_LeavesNoTempFileBehind_OnSuccess()
    {
        var outPath = Path.Combine(_dir, "cards.lfidx");
        AtomicIndexWriter.WriteAndPublish(outPath, [MakeEntry("a")]);

        var leftovers = Directory.GetFiles(_dir, ".*.tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void WriteAndPublish_FailureDuringWrite_LeavesOutUntouched_AndNoTempFile()
    {
        var outPath = Path.Combine(_dir, "cards.lfidx");
        File.WriteAllText(outPath, "PRE-EXISTING CONTENT");

        // A `write` stub that writes SOMETHING to the temp path (so there
        // really is a partial file to worry about) and then throws --
        // proving the failure happens strictly before the rename, not that
        // HashIndexFile.Write itself happens to validate its arguments
        // before opening any file.
        void FaultyWrite(string tempPath, IReadOnlyList<HashIndexEntry> _)
        {
            File.WriteAllText(tempPath, "PARTIAL GARBAGE");
            throw new InvalidOperationException("simulated failure mid-write");
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => AtomicIndexWriter.WriteAndPublish(outPath, [MakeEntry("a")], FaultyWrite));
        Assert.Equal("simulated failure mid-write", ex.Message);

        Assert.Equal("PRE-EXISTING CONTENT", File.ReadAllText(outPath));
        var leftovers = Directory.GetFiles(_dir, ".*.tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void WriteAndPublish_FirstBuild_FailureDuringWrite_NeverCreatesOut()
    {
        var outPath = Path.Combine(_dir, "cards.lfidx");

        void FaultyWrite(string tempPath, IReadOnlyList<HashIndexEntry> _)
        {
            File.WriteAllText(tempPath, "PARTIAL GARBAGE");
            throw new InvalidOperationException("simulated failure mid-write");
        }

        Assert.Throws<InvalidOperationException>(
            () => AtomicIndexWriter.WriteAndPublish(outPath, [MakeEntry("a")], FaultyWrite));

        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void WriteAndPublish_VerificationCatchesEntryCountMismatch()
    {
        var outPath = Path.Combine(_dir, "cards.lfidx");

        // A `write` stub that silently drops an entry -- simulating a
        // writer bug where fewer entries land on disk than were asked for.
        // AtomicIndexWriter's own re-read verification must catch this
        // BEFORE the rename, exactly like a real HashIndexFile.Write defect
        // would need to be caught.
        void DroppingWrite(string tempPath, IReadOnlyList<HashIndexEntry> entries) =>
            HashIndexFile.Write(tempPath, entries.Take(entries.Count - 1).ToList());

        Assert.Throws<InvalidOperationException>(
            () => AtomicIndexWriter.WriteAndPublish(outPath, [MakeEntry("a"), MakeEntry("b")], DroppingWrite));

        Assert.False(File.Exists(outPath));
    }
}
