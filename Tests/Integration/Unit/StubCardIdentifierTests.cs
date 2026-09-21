using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubCardIdentifierTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    [Fact]
    public void Name_IsStub()
    {
        var identifier = new StubCardIdentifier();

        Assert.Equal("Stub", identifier.Name);
    }

    [Fact]
    public void Identify_ReturnsCandidatesInAscendingDistance_WithDistinctOracleIds()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { 300, 50, 150 } };

        var candidates = identifier.Identify(MakeCard(), maxCandidates: 3);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(new[] { 50, 150, 300 }, candidates.Select(c => c.Distance));
        Assert.Equal(candidates.Count, candidates.Select(c => c.OracleId).Distinct().Count());
    }

    [Fact]
    public void Identify_HonoursMaxCandidates()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { 10, 20, 30, 40, 50 } };

        var candidates = identifier.Identify(MakeCard(), maxCandidates: 2);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Identify_NeverFiltersByThreshold_ReturnsCandidatesBeyondOkDistanceToo()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { OkDistance + 500 } };

        var candidates = identifier.Identify(MakeCard(), maxCandidates: 1);

        Assert.Single(candidates);
        Assert.Equal(OkDistance + 500, candidates[0].Distance);
    }

    [Fact]
    public void Identify_EmptyNextDistances_ReturnsNoCandidates()
    {
        var identifier = new StubCardIdentifier { NextDistances = Array.Empty<int>() };

        var candidates = identifier.Identify(MakeCard(), maxCandidates: 5);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Identify_ArtworkIdIsAlwaysNull()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { 10 } };

        var candidates = identifier.Identify(MakeCard(), maxCandidates: 1);

        Assert.Null(candidates[0].ArtworkId);
    }

    [Fact]
    public void ConfiguredDistance_BelowGood_DrivesTile_ToIncludedNotLowConfidence()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { GoodDistance - 10 } };
        var candidates = identifier.Identify(MakeCard(), maxCandidates: 1);

        var tile = new CohortTile(MakeCard(), candidates, GoodDistance, OkDistance);

        Assert.Equal(TileState.Included, tile.State);
        Assert.False(tile.IsLowConfidence);
    }

    [Fact]
    public void ConfiguredDistance_BetweenGoodAndOk_DrivesTile_ToIncludedLowConfidence()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { GoodDistance + 10 } };
        var candidates = identifier.Identify(MakeCard(), maxCandidates: 1);

        var tile = new CohortTile(MakeCard(), candidates, GoodDistance, OkDistance);

        Assert.Equal(TileState.Included, tile.State);
        Assert.True(tile.IsLowConfidence);
    }

    [Fact]
    public void ConfiguredDistance_BeyondOk_DrivesTile_ToUnresolved()
    {
        var identifier = new StubCardIdentifier { NextDistances = new[] { OkDistance + 10 } };
        var candidates = identifier.Identify(MakeCard(), maxCandidates: 1);

        var tile = new CohortTile(MakeCard(), candidates, GoodDistance, OkDistance);

        Assert.Equal(TileState.Unresolved, tile.State);
    }

    private static RectifiedCard MakeCard() =>
        new(new byte[488 * 680 * 3], stride: 488 * 3, layout: PixelLayout.Bgr24,
            sourceQuad: new CardQuad(
                new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));
}
