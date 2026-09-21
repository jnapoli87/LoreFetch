using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class CardQuadTests
{
    [Fact]
    public void Portrait_AreaAndAspectRatio_MatchKnownDimensions()
    {
        // A 100 x 139.7 px axis-aligned rectangle: card proportions (1:1.397)
        // in portrait orientation (taller than wide).
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(100, 0),
            BR: new PointF2(100, 139.7f),
            BL: new PointF2(0, 139.7f));

        Assert.Equal(100f * 139.7f, quad.AreaPx, precision: 1);
        Assert.True(quad.AspectRatio > 1f);
        Assert.Equal(1.397f, quad.AspectRatio, precision: 2);
    }

    [Fact]
    public void Landscape_AreaAndAspectRatio_MatchKnownDimensions_AndStayAboveOne()
    {
        // The same card, rotated 90 degrees: wider than tall. AspectRatio
        // must still read ~1.397 (long/short), never ~0.716 (short/long).
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(139.7f, 0),
            BR: new PointF2(139.7f, 100),
            BL: new PointF2(0, 100));

        Assert.Equal(139.7f * 100f, quad.AreaPx, precision: 1);
        Assert.True(quad.AspectRatio > 1f);
        Assert.Equal(1.397f, quad.AspectRatio, precision: 2);
    }
}
