using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class CohortTileTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    private static RectifiedCard MakeImage() =>
        new(new byte[488 * 680 * 3], stride: 488 * 3, layout: PixelLayout.Bgr24,
            sourceQuad: new CardQuad(
                new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));

    private static CardCandidate Candidate(int distance, string oracleId = "oracle-1", string name = "Forest", string? artworkId = null) =>
        new(oracleId, name, distance, artworkId);

    private static CohortTile MakeTile(IReadOnlyList<CardCandidate> candidates) =>
        new(MakeImage(), candidates, GoodDistance, OkDistance);

    [Fact]
    public void InitialState_BestWithinGood_IsIncludedNotLowConfidence()
    {
        var candidates = new[] { Candidate(GoodDistance) }; // exactly AT the boundary
        var tile = MakeTile(candidates);

        Assert.Equal(TileState.Included, tile.State);
        Assert.Equal(new OracleEntry("oracle-1", "Forest"), tile.Chosen);
        Assert.Equal(GoodDistance, tile.ChosenDistance);
        Assert.False(tile.IsLowConfidence);
    }

    [Fact]
    public void InitialState_BestWithinGood_ExposesChosenArtworkId()
    {
        var candidates = new[] { Candidate(GoodDistance, artworkId: "art-1") };
        var tile = MakeTile(candidates);

        Assert.Equal("art-1", tile.ChosenArtworkId);
    }

    [Fact]
    public void InitialState_BestWithinOkButBeyondGood_IsIncludedAndLowConfidence()
    {
        var candidates = new[] { Candidate(OkDistance) }; // exactly AT the ok boundary
        var tile = MakeTile(candidates);

        Assert.Equal(TileState.Included, tile.State);
        Assert.Equal(new OracleEntry("oracle-1", "Forest"), tile.Chosen);
        Assert.Equal(OkDistance, tile.ChosenDistance);
        Assert.True(tile.IsLowConfidence);
    }

    [Fact]
    public void InitialState_BestBeyondOk_IsUnresolved()
    {
        var candidates = new[] { Candidate(OkDistance + 1) };
        var tile = MakeTile(candidates);

        Assert.Equal(TileState.Unresolved, tile.State);
        Assert.Null(tile.Chosen);
        Assert.Null(tile.ChosenDistance);
        Assert.False(tile.IsLowConfidence);
    }

    [Fact]
    public void InitialState_BestBeyondOk_ChosenArtworkIdIsNull()
    {
        // Same null rule as ChosenDistance: an Unresolved tile proposes
        // nothing, art id included, even when the best candidate carried one.
        var candidates = new[] { Candidate(OkDistance + 1, artworkId: "art-1") };
        var tile = MakeTile(candidates);

        Assert.Null(tile.ChosenArtworkId);
    }

    [Fact]
    public void InitialState_NoCandidates_IsUnresolved()
    {
        var tile = MakeTile(Array.Empty<CardCandidate>());

        Assert.Equal(TileState.Unresolved, tile.State);
        Assert.Null(tile.Chosen);
        Assert.Null(tile.ChosenDistance);
        Assert.False(tile.IsLowConfidence);
    }

    [Fact]
    public void InitialState_NoCandidates_ChosenArtworkIdIsNull()
    {
        var tile = MakeTile(Array.Empty<CardCandidate>());

        Assert.Null(tile.ChosenArtworkId);
    }

    [Fact]
    public void ToggleExcluded_FromIncluded_RoundTripsBackToIncluded()
    {
        var tile = MakeTile(new[] { Candidate(GoodDistance) });
        Assert.Equal(TileState.Included, tile.State);

        tile.ToggleExcluded();
        Assert.Equal(TileState.Excluded, tile.State);

        tile.ToggleExcluded();
        Assert.Equal(TileState.Included, tile.State);
    }

    [Fact]
    public void ToggleExcluded_FromManuallySet_RoundTripsBackToManuallySetNotIncluded()
    {
        var tile = MakeTile(new[] { Candidate(GoodDistance) });
        tile.SetManually(new OracleEntry("oracle-2", "Island"));
        Assert.Equal(TileState.ManuallySet, tile.State);

        tile.ToggleExcluded();
        Assert.Equal(TileState.Excluded, tile.State);

        tile.ToggleExcluded();
        Assert.Equal(TileState.ManuallySet, tile.State);
        Assert.Equal(new OracleEntry("oracle-2", "Island"), tile.Chosen);
    }

    [Fact]
    public void ToggleExcluded_OnUnresolved_IsNoOp()
    {
        var tile = MakeTile(Array.Empty<CardCandidate>());
        Assert.Equal(TileState.Unresolved, tile.State);

        tile.ToggleExcluded();

        Assert.Equal(TileState.Unresolved, tile.State);
    }

    [Fact]
    public void SetManually_SetsManuallySetChosenAndNullsChosenDistance()
    {
        var tile = MakeTile(new[] { Candidate(GoodDistance) });

        tile.SetManually(new OracleEntry("oracle-2", "Island"));

        Assert.Equal(TileState.ManuallySet, tile.State);
        Assert.Equal(new OracleEntry("oracle-2", "Island"), tile.Chosen);
        Assert.Null(tile.ChosenDistance);
    }

    [Fact]
    public void SetManually_NullsChosenArtworkId()
    {
        // A manual pick names a CARD, not an art — recording the hash's guess
        // would attribute a printing to a choice made on other grounds.
        var tile = MakeTile(new[] { Candidate(GoodDistance, artworkId: "art-1") });

        tile.SetManually(new OracleEntry("oracle-2", "Island"));

        Assert.Null(tile.ChosenArtworkId);
    }

    [Fact]
    public void Clear_FromManuallySet_WithBestWithinOk_ReturnsToIncluded()
    {
        var candidates = new[] { Candidate(OkDistance) };
        var tile = MakeTile(candidates);
        tile.SetManually(new OracleEntry("oracle-2", "Island"));

        tile.Clear();

        Assert.Equal(TileState.Included, tile.State);
        Assert.Equal(new OracleEntry("oracle-1", "Forest"), tile.Chosen);
        Assert.Equal(OkDistance, tile.ChosenDistance);
        Assert.True(tile.IsLowConfidence);
    }

    [Fact]
    public void Clear_FromManuallySet_RestoresChosenArtworkIdToHashValue()
    {
        // Clear() re-runs ProposeFromHash rather than remembering the
        // pre-manual value, so this proves the restore is computed, not cached.
        var candidates = new[] { Candidate(OkDistance, artworkId: "art-1") };
        var tile = MakeTile(candidates);
        tile.SetManually(new OracleEntry("oracle-2", "Island"));
        Assert.Null(tile.ChosenArtworkId); // sanity: the manual set nulled it first

        tile.Clear();

        Assert.Equal("art-1", tile.ChosenArtworkId);
    }

    [Fact]
    public void Clear_FromManuallySet_WithBestBeyondOk_ReturnsToUnresolved()
    {
        var candidates = new[] { Candidate(OkDistance + 1) };
        var tile = MakeTile(candidates);
        tile.SetManually(new OracleEntry("oracle-2", "Island"));

        tile.Clear();

        Assert.Equal(TileState.Unresolved, tile.State);
        Assert.Null(tile.Chosen);
        Assert.Null(tile.ChosenDistance);
        Assert.False(tile.IsLowConfidence);
    }

    [Theory]
    [InlineData(TileState.Included)]
    [InlineData(TileState.Excluded)]
    [InlineData(TileState.Unresolved)]
    public void Clear_IsNoOp_UnlessStateIsManuallySet(TileState startingState)
    {
        var candidates = startingState == TileState.Unresolved
            ? Array.Empty<CardCandidate>()
            : new[] { Candidate(GoodDistance) };
        var tile = MakeTile(candidates);

        if (startingState == TileState.Excluded)
        {
            tile.ToggleExcluded();
        }

        Assert.Equal(startingState, tile.State);
        var chosenBefore = tile.Chosen;
        var chosenDistanceBefore = tile.ChosenDistance;
        var lowConfidenceBefore = tile.IsLowConfidence;

        tile.Clear();

        Assert.Equal(startingState, tile.State);
        Assert.Equal(chosenBefore, tile.Chosen);
        Assert.Equal(chosenDistanceBefore, tile.ChosenDistance);
        Assert.Equal(lowConfidenceBefore, tile.IsLowConfidence);
    }
}

public class CohortTests
{
    [Fact]
    public void Constructed_ExposesTilesIdAndReason()
    {
        var image = new RectifiedCard(
            new byte[488 * 680 * 3], stride: 488 * 3, layout: PixelLayout.Bgr24,
            sourceQuad: new CardQuad(
                new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));
        var tile = new CohortTile(
            image,
            new[] { new CardCandidate("oracle-1", "Forest", 50, ArtworkId: null) },
            goodDistance: 100,
            okDistance: 200);
        var id = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;

        var cohort = new Cohort(id, capturedAt, expectedCount: 1, CaptureReason.Manual, new[] { tile });

        Assert.Equal(id, cohort.Id);
        Assert.Equal(capturedAt, cohort.CapturedAt);
        Assert.Equal(1, cohort.ExpectedCount);
        Assert.Equal(CaptureReason.Manual, cohort.Reason);
        Assert.Single(cohort.Tiles);
        Assert.Same(tile, cohort.Tiles[0]);
    }
}
