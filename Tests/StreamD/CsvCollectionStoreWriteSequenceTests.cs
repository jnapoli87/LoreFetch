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

    [Fact]
    public async Task Commit_WhenTheTargetIsLockedWithNoSharing_ThrowsAndLeavesTheOriginalByteIdentical_WithNoStrayTempFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var first = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));
        await store.CommitCohortAsync(first, ct);
        var originalBytes = await File.ReadAllBytesAsync(workspace.CollectionPath, ct);

        var locker = new FileStream(workspace.CollectionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
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

        var strayTempFiles = Directory.GetFiles(workspace.Directory, "*.tmp");
        Assert.Empty(strayTempFiles);
    }
}
