using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.StreamD;

public class CsvCollectionStoreCommitTests
{
    private static CsvCollectionStore NewStore(TempWorkspace workspace) =>
        new(workspace.CollectionPath, new CapturingLogger<CsvCollectionStore>());

    [Fact]
    public async Task ExcludedTile_IsNotWritten()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var included = CohortSupport.IncludedTile("oracle-1", "Forest");
        var excluded = CohortSupport.ExcludedTile("oracle-2", "Island");
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, included, excluded);

        var cardsCommitted = await store.CommitCohortAsync(cohort, CancellationToken.None);

        Assert.Equal(1, cardsCommitted);
        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("oracle-1", row.OracleId);
    }

    [Fact]
    public async Task UnresolvedTile_IsNotWritten()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.UnresolvedTile());

        var cardsCommitted = await store.CommitCohortAsync(cohort, CancellationToken.None);

        Assert.Equal(0, cardsCommitted);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManuallySetTile_CommitsSourceManual_WithNullDistanceAndNullArtworkId()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.ManualTile("oracle-1", "Forest"));

        await store.CommitCohortAsync(cohort, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(RowSource.Manual, row.Source);
        Assert.Null(row.BestMatchDistance);
        Assert.Null(row.ArtworkId);
    }

    [Fact]
    public async Task ManuallySetTile_WithAForcedNonNullChosenArtworkId_StillCommitsNullArtworkId()
    {
        // The S0.6b decision, made concrete: CohortTile.SetManually already
        // nulls ChosenArtworkId on the tile itself, so a naive "trust the
        // tile" implementation and the correct "derive from State"
        // implementation are INDISTINGUISHABLE through the tile's normal
        // public API — the test above this one passes either way. Reflection
        // forces the tile into the state a future CohortTile bug (or a
        // deserialised/replayed tile) could produce, so this test actually
        // exercises the store's own defence rather than relying on
        // CohortTile's invariant holding forever.
        var tile = CohortSupport.ManualTile("oracle-1", "Forest");
        var setter = typeof(CohortTile).GetProperty(nameof(CohortTile.ChosenArtworkId))!.GetSetMethod(nonPublic: true)!;
        setter.Invoke(tile, ["should-never-appear"]);

        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, tile);

        await store.CommitCohortAsync(cohort, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(RowSource.Manual, row.Source);
        Assert.Null(row.ArtworkId);
    }

    [Fact]
    public async Task IncludedTile_CommitsSourceHash_WithChosenDistanceAndChosenArtworkId()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(
            TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest", distance: 37, artworkId: "art-1"));

        await store.CommitCohortAsync(cohort, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(RowSource.Hash, row.Source);
        Assert.Equal(37, row.BestMatchDistance);
        Assert.Equal("art-1", row.ArtworkId);
    }

    [Fact]
    public async Task NineTilesOfOneOracleId_InOneCohort_Returns9_AndProducesOneRowOfQuantity9()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var tiles = Enumerable.Range(0, 9)
            .Select(_ => CohortSupport.IncludedTile("forest-oracle-id", "Forest"))
            .ToArray();
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, tiles);

        var cardsCommitted = await store.CommitCohortAsync(cohort, CancellationToken.None);

        Assert.Equal(9, cardsCommitted);
        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(9, row.Quantity);
    }

    [Fact]
    public async Task SameCardCommittedAcrossTwoCohorts_YieldsOneRowWithQuantity2()
    {
        // The blank-condition round trip from D0 item 1: Condition is always
        // null through the commit path, so this only proves the dedup key
        // matches ACROSS commits (not just within one) if null really does
        // compare equal to null every time — the bug this guards against is
        // a store that reads "" back from disk on the second commit and
        // treats it as a different card than the null it just wrote.
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var first = CohortSupport.Cohort(TestSupport.SampleTimestamp, CohortSupport.IncludedTile("oracle-1", "Forest"));
        var second = CohortSupport.Cohort(TestSupport.SampleTimestamp.AddMinutes(1), CohortSupport.IncludedTile("oracle-1", "Forest"));

        await store.CommitCohortAsync(first, CancellationToken.None);
        await store.CommitCohortAsync(second, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(2, row.Quantity);
        Assert.Null(row.Condition);
    }

    [Fact]
    public async Task AgreeingArtworkIds_WithinOneCohort_AreKept()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(
            TestSupport.SampleTimestamp,
            CohortSupport.IncludedTile("oracle-1", "Forest", artworkId: "art-1"),
            CohortSupport.IncludedTile("oracle-1", "Forest", artworkId: "art-1"));

        await store.CommitCohortAsync(cohort, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("art-1", row.ArtworkId);
        Assert.Equal(2, row.Quantity);
    }

    [Fact]
    public async Task DisagreeingArtworkIds_WithinOneCohort_AreNulled()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(
            TestSupport.SampleTimestamp,
            CohortSupport.IncludedTile("oracle-1", "Forest", artworkId: "art-1"),
            CohortSupport.IncludedTile("oracle-1", "Forest", artworkId: "art-2"));

        await store.CommitCohortAsync(cohort, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Null(row.ArtworkId);
        Assert.Equal(2, row.Quantity);
    }

    [Fact]
    public async Task IncludedOrManuallySetTile_WithNullChosen_ThrowsRatherThanWritingAnEmptyRow()
    {
        // CohortTile's own public API (constructor, SetManually) can never
        // produce Included/ManuallySet with a null Chosen — the sealed class
        // enforces that invariant structurally. Reflection is the only way
        // to reach this branch at all, which is itself the point: this is a
        // defence against a BROKEN invariant (a future bug elsewhere), not
        // a reachable data case. See CsvCollectionStore's doc comment.
        var tile = CohortSupport.IncludedTile("oracle-1", "Forest");
        var setter = typeof(CohortTile).GetProperty(nameof(CohortTile.Chosen))!.GetSetMethod(nonPublic: true)!;
        setter.Invoke(tile, [null]);

        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        var cohort = CohortSupport.Cohort(TestSupport.SampleTimestamp, tile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitCohortAsync(cohort, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCommits_OfTheSameCard_NeverLoseAnIncrement()
    {
        using var workspace = new TempWorkspace();
        var store = NewStore(workspace);
        const int concurrentCommits = 8;

        var tasks = Enumerable.Range(0, concurrentCommits).Select(i =>
        {
            var cohort = CohortSupport.Cohort(
                TestSupport.SampleTimestamp.AddSeconds(i), CohortSupport.IncludedTile("oracle-1", "Forest"));
            return store.CommitCohortAsync(cohort, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrentCommits, results.Sum());
        var row = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(concurrentCommits, row.Quantity);
    }
}
