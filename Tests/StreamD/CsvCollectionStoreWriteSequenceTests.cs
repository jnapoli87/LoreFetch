using System.Collections.Concurrent;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.StreamD;

public class CsvCollectionStoreWriteSequenceTests
{
    private static CsvCollectionStore NewStore(TempWorkspace workspace) =>
        new(workspace.CollectionPath, new CapturingLogger<CsvCollectionStore>());

    [Fact]
    public async Task ListAsync_OnAMissingFile_ReturnsAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);

        var rows = await store.ListAsync(ct);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Commit_CreatesTheTempFileInTheTargetsOwnDirectory()
    {
        // Observed via a real FileSystemWatcher rather than asserted on
        // code, per the work package: proves the temp file actually lands
        // next to collection.csv, not in Path.GetTempPath(), which is the
        // one thing the whole atomic-rename guarantee depends on.
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));

        var createdPaths = new ConcurrentBag<string>();
        using var signal = new SemaphoreSlim(0);
        using var watcher = new FileSystemWatcher(workspace.Directory)
        {
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Created += (_, e) =>
        {
            createdPaths.Add(e.FullPath);
            signal.Release();
        };
        watcher.EnableRaisingEvents = true;

        await store.CommitCohortAsync(cohort, ct);
        await signal.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Contains(createdPaths, p =>
            p.EndsWith(".tmp", StringComparison.Ordinal) &&
            string.Equals(Path.GetDirectoryName(p), workspace.Directory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecondCommit_LeavesTheFirstCommitsContentInBak()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var first = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));
        await store.CommitCohortAsync(first, ct);
        var contentAfterFirstCommit = await File.ReadAllBytesAsync(workspace.CollectionPath, ct);

        var second = CohortSupport.Cohort(TestSupport.SampleTimestamp.AddMinutes(1), CohortSupport.IncludedTile("oracle-2", "Island"));
        await store.CommitCohortAsync(second, ct);

        var backupPath = workspace.CollectionPath + ".bak";
        Assert.True(File.Exists(backupPath));
        var backupContent = await File.ReadAllBytesAsync(backupPath, ct);
        Assert.Equal(contentAfterFirstCommit, backupContent);
    }

    /// The plan's literal D3 case. Under FileShare.None the commit fails at
    /// its READ step, before any temp file exists — so this proves the
    /// exception type and the untouched original, and the ReadWrite test
    /// below is the one that proves temp-file cleanup on the write path.
    [Fact]
    public async Task Commit_WhenTheTargetIsLockedWithNoSharing_ThrowsAndLeavesTheOriginalByteIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var first = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));
        await store.CommitCohortAsync(first, ct);
        var originalBytes = await File.ReadAllBytesAsync(workspace.CollectionPath, ct);

        using (new FileStream(workspace.CollectionPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = CohortSupport.Cohort(TestSupport.SampleTimestamp.AddMinutes(1), CohortSupport.IncludedTile("oracle-2", "Island"));
            await Assert.ThrowsAsync<CollectionStoreException>(() => store.CommitCohortAsync(second, ct));
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workspace.CollectionPath, ct));
        Assert.Equal(new[] { "collection.csv" }, Directory.GetFiles(workspace.Directory).Select(Path.GetFileName).Order().ToArray());
    }

    // macOS/Unix: rename(2) replaces an open file regardless of open handles,
    // so File.Move(overwrite:true) SUCCEEDS even when a reader holds the file
    // with FileShare.ReadWrite — no CollectionStoreException is thrown and
    // the test would fail. The store code is correct; this exercises a
    // Windows-specific platform guarantee (locker lacks FileShare.Delete →
    // File.Move throws), so it is traited WindowsOnly.
    [Trait("Category", "WindowsOnly")]
    [Fact]
    public async Task Commit_WhenTheTargetCannotBeReplaced_ThrowsAndLeavesTheOriginalByteIdentical_WithNoStrayTempFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var first = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));
        await store.CommitCohortAsync(first, ct);
        var originalBytes = await File.ReadAllBytesAsync(workspace.CollectionPath, ct);

        // FileShare.None would make the READ half of the commit (opened
        // FileAccess.Read/FileShare.Read) fail before WriteAtomicallyAsync
        // is ever reached — proven by an earlier version of this test, which
        // stayed green with the temp-file cleanup call deleted entirely,
        // because that whole code path was unreachable under FileShare.None.
        // FileShare.ReadWrite (no Delete) is the scenario that actually
        // exercises the write path: the read and the .bak copy both succeed
        // — neither needs Delete access — but the final File.Move, which
        // must replace the still-open target, fails on Windows because the
        // locker was never granted FileShare.Delete. That is also the more
        // realistic "Excel has it open" case: Excel does not deny read
        // access to other processes.
        var locker = new FileStream(workspace.CollectionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var second = CohortSupport.Cohort(TestSupport.SampleTimestamp.AddMinutes(1), CohortSupport.IncludedTile("oracle-2", "Island"));

            await Assert.ThrowsAsync<CollectionStoreException>(() => store.CommitCohortAsync(second, ct));
        }
        finally
        {
            locker.Dispose();
        }

        var bytesAfterFailedCommit = await File.ReadAllBytesAsync(workspace.CollectionPath, ct);
        Assert.Equal(originalBytes, bytesAfterFailedCommit);

        // Exactly the pre-existing files (target + the .bak the failed
        // commit's own copy step created) — nothing else. A bare
        // Assert.Empty on a "*.tmp" glob passed even with the cleanup call
        // deleted, because under the old FileShare.None lock the temp file
        // was never created in the first place; asserting the full,
        // sorted directory listing makes it impossible for that kind of
        // "nothing to find" false pass to hide a leak again.
        var expectedFileNames = new[] { "collection.csv", "collection.csv.bak" };
        var actualFileNames = Directory.GetFiles(workspace.Directory).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(expectedFileNames, actualFileNames);
    }
}
