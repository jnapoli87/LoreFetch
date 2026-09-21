using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubCollectionStoreTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    [Fact]
    public async Task CommitCohortAsync_SameCardTwice_BlankCondition_YieldsOneRow_QuantityTwo()
    {
        var store = new StubCollectionStore();
        var cohort = MakeCohort(
            MakeIncludedTile("oracle-forest", "Forest", distance: 10),
            MakeIncludedTile("oracle-forest", "Forest", distance: 20));

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Equal("oracle-forest", rows[0].OracleId);
        Assert.Null(rows[0].Condition); // null is the only representation of "unassessed" — never ""
    }

    [Fact]
    public async Task CommitCohortAsync_NineTilesSameCard_ReturnsNine_NotOne()
    {
        var store = new StubCollectionStore();
        var tiles = Enumerable.Range(0, 9)
            .Select(i => MakeIncludedTile("oracle-forest", "Forest", distance: 10 + i))
            .ToArray();
        var cohort = MakeCohort(tiles);

        var cardsCommitted = await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);

        Assert.Equal(9, cardsCommitted);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(rows);
        Assert.Equal(9, rows[0].Quantity);
    }

    [Fact]
    public async Task CommitCohortAsync_ExcludedAndUnresolvedTiles_AreAbsentFromListAsync()
    {
        var store = new StubCollectionStore();
        var included = MakeIncludedTile("oracle-forest", "Forest", distance: 10);
        var excluded = MakeIncludedTile("oracle-island", "Island", distance: 10);
        excluded.ToggleExcluded();
        var unresolved = MakeUnresolvedTile();
        var cohort = MakeCohort(included, excluded, unresolved);

        var cardsCommitted = await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, cardsCommitted);
        Assert.Single(rows);
        Assert.Equal("oracle-forest", rows[0].OracleId);
    }

    [Fact]
    public async Task CommitCohortAsync_ManuallySetTile_CommitsAsSourceManual_WithNullBestMatchDistance()
    {
        var store = new StubCollectionStore();
        var tile = MakeUnresolvedTile();
        tile.SetManually(new OracleEntry("oracle-island", "Island"));
        var cohort = MakeCohort(tile);

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(RowSource.Manual, rows[0].Source);
        Assert.Null(rows[0].BestMatchDistance);
    }

    [Fact]
    public async Task CommitCohortAsync_IncludedTile_CommitsAsSourceHash_WithChosenDistance()
    {
        var store = new StubCollectionStore();
        var tile = MakeIncludedTile("oracle-forest", "Forest", distance: 42);
        var cohort = MakeCohort(tile);

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(RowSource.Hash, rows[0].Source);
        Assert.Equal(42, rows[0].BestMatchDistance);
    }

    [Fact]
    public async Task CommitCohortAsync_IncludedTile_WritesArtworkIdOnSourceHashRow()
    {
        var store = new StubCollectionStore();
        var tile = MakeIncludedTile("oracle-forest", "Forest", distance: 42, artworkId: "art-1");
        var cohort = MakeCohort(tile);

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(RowSource.Hash, rows[0].Source);
        Assert.Equal("art-1", rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_ManuallySetTile_WritesNullArtworkId()
    {
        // Starts from a candidate that DID carry an art id, so this proves
        // SetManually's null-out survives to the committed row rather than
        // the row merely being null because nothing was ever proposed.
        var store = new StubCollectionStore();
        var tile = MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-1");
        tile.SetManually(new OracleEntry("oracle-island", "Island"));
        var cohort = MakeCohort(tile);

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(RowSource.Manual, rows[0].Source);
        Assert.Null(rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_TwoTilesOfOneCard_AgreeingArtworkIds_PreservesArtworkId()
    {
        // The ordinary case: one card scanned twice in one cohort is the same
        // art both times, so the fold must keep the non-null ArtworkId.
        var store = new StubCollectionStore();
        var cohort = MakeCohort(
            MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-1"),
            MakeIncludedTile("oracle-forest", "Forest", distance: 20, artworkId: "art-1"));

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Equal("art-1", rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_TwoTilesOfOneCard_DisagreeingArtworkIds_NullsArtworkId()
    {
        // Two different printings' art matched under the same oracle card —
        // agree-or-null means the merged row admits it does not know, rather
        // than picking whichever tile happened to fold in last.
        var store = new StubCollectionStore();
        var cohort = MakeCohort(
            MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-1"),
            MakeIncludedTile("oracle-forest", "Forest", distance: 20, artworkId: "art-2"));

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Null(rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_AgainstSeededRow_DifferingArtworkId_NullsMergedArtworkId()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-forest", "Forest", Quantity: 1, Condition: null,
            LastScannedAt: DateTimeOffset.UtcNow, BestMatchDistance: 50, Source: RowSource.Hash,
            ArtworkId: "art-1"));

        var cohort = MakeCohort(MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-2"));
        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Null(rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_AgainstSeededRow_AgreeingArtworkId_PreservesArtworkId()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-forest", "Forest", Quantity: 1, Condition: null,
            LastScannedAt: DateTimeOffset.UtcNow, BestMatchDistance: 50, Source: RowSource.Hash,
            ArtworkId: "art-1"));

        var cohort = MakeCohort(MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-1"));
        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Equal("art-1", rows[0].ArtworkId);
    }

    [Fact]
    public async Task CommitCohortAsync_TwoMergingManualRows_BothNullArtworkId_StaysNull()
    {
        // The trap agree-or-null must not fall into: null vs null must read
        // as agreement, not as a disagreement that happens to render the
        // same as agreement's result. Seeded row is Manual (null); the
        // committed tile is manually set from a candidate that DID carry an
        // art id, so a naive "any null means disagreement" implementation
        // would still show null here — this only distinguishes from that if
        // paired with the disagreement cases above, which show a genuine
        // difference producing null too.
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-forest", "Forest", Quantity: 1, Condition: null,
            LastScannedAt: DateTimeOffset.UtcNow, BestMatchDistance: null, Source: RowSource.Manual,
            ArtworkId: null));

        var tile = MakeIncludedTile("oracle-forest", "Forest", distance: 10, artworkId: "art-1");
        tile.SetManually(new OracleEntry("oracle-forest", "Forest"));
        var cohort = MakeCohort(tile);

        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);
        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Equal(RowSource.Manual, rows[0].Source);
        Assert.Null(rows[0].ArtworkId);
    }

    [Fact]
    public async Task ArmNextCommitToThrow_Throws_AndLeavesContentsUnchanged()
    {
        var store = new StubCollectionStore();
        await store.CommitCohortAsync(MakeCohort(MakeIncludedTile("oracle-forest", "Forest", 10)), TestContext.Current.CancellationToken);
        var before = await store.ListAsync(TestContext.Current.CancellationToken);

        store.ArmNextCommitToThrow();

        await Assert.ThrowsAsync<CollectionStoreException>(
            () => store.CommitCohortAsync(MakeCohort(MakeIncludedTile("oracle-island", "Island", 10)), TestContext.Current.CancellationToken));

        var after = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ArmNextCommitToThrow_OnlyAffectsTheNextCommit()
    {
        var store = new StubCollectionStore();
        store.ArmNextCommitToThrow();

        await Assert.ThrowsAsync<CollectionStoreException>(
            () => store.CommitCohortAsync(MakeCohort(MakeIncludedTile("oracle-forest", "Forest", 10)), TestContext.Current.CancellationToken));

        // The retry succeeds.
        var cardsCommitted = await store.CommitCohortAsync(
            MakeCohort(MakeIncludedTile("oracle-forest", "Forest", 10)), TestContext.Current.CancellationToken);
        Assert.Equal(1, cardsCommitted);
    }

    /// Dedup identity is OracleId + Condition, not OracleId alone. Nothing
    /// upstream of the store can produce a non-null Condition in v1, so this
    /// seeds one directly to prove a same-OracleId row under a *different*
    /// Condition is left alone rather than merged into it.
    [Fact]
    public async Task CommitCohortAsync_DoesNotMergeAcrossDifferentConditions()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-forest", "Forest", Quantity: 1, Condition: "NM",
            LastScannedAt: DateTimeOffset.UtcNow, BestMatchDistance: null, Source: RowSource.Manual,
            ArtworkId: null));

        var cohort = MakeCohort(MakeIncludedTile("oracle-forest", "Forest", distance: 10));
        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        var seeded = Assert.Single(rows, r => r.Condition == "NM");
        Assert.Equal(1, seeded.Quantity);
        var scanned = Assert.Single(rows, r => r.Condition is null);
        Assert.Equal(1, scanned.Quantity);
    }

    /// The inverse of the above: a null Condition is not the same key as an
    /// empty-string Condition. "" should never occur in practice — the
    /// contract says null is the only representation of "unassessed" — but
    /// the dedup key must not silently normalise one to the other (e.g. via
    /// `Condition ?? ""` on one side of a comparison), or a row that
    /// shouldn't exist would spuriously absorb a legitimate unassessed scan.
    [Fact]
    public async Task CommitCohortAsync_DoesNotMergeNullConditionWithEmptyStringCondition()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-forest", "Forest", Quantity: 1, Condition: "",
            LastScannedAt: DateTimeOffset.UtcNow, BestMatchDistance: null, Source: RowSource.Manual,
            ArtworkId: null));

        var cohort = MakeCohort(MakeIncludedTile("oracle-forest", "Forest", distance: 10));
        await store.CommitCohortAsync(cohort, TestContext.Current.CancellationToken);

        var rows = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        var seeded = Assert.Single(rows, r => r.Condition == "");
        Assert.Equal(1, seeded.Quantity);
        var scanned = Assert.Single(rows, r => r.Condition is null);
        Assert.Equal(1, scanned.Quantity);
    }

    private static CohortTile MakeIncludedTile(string oracleId, string oracleName, int distance, string? artworkId = null) =>
        new(MakeImage(), new[] { new CardCandidate(oracleId, oracleName, distance, artworkId) },
            GoodDistance, OkDistance);

    private static CohortTile MakeUnresolvedTile() =>
        new(MakeImage(), Array.Empty<CardCandidate>(), GoodDistance, OkDistance);

    private static Cohort MakeCohort(params CohortTile[] tiles) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, tiles.Length, CaptureReason.Manual, tiles);

    private static RectifiedCard MakeImage() =>
        new(new byte[488 * 680 * 3], stride: 488 * 3, layout: PixelLayout.Bgr24,
            sourceQuad: new CardQuad(
                new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));
}
