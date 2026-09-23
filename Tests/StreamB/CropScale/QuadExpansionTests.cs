using LoreFetch.Core.Abstractions;
using LoreFetch.Lab.CropScale;
using Xunit;

namespace LoreFetch.Tests.StreamB.CropScale;

/// Package E1a: `QuadExpansion` is diagnostic tooling for the crop-
/// expansion experiment, not part of the shipping detect/rectify/identify
/// path (see that type's own doc comment), so these tests pin its GEOMETRY
/// directly -- the centroid stays fixed, each axis scales independently
/// regardless of the quad's own rotation, and factor 1.0 is an EXACT
/// identity (not merely close) -- rather than anything about hash distance
/// or identification outcome, which the command-level experiment measures
/// separately against real frames.
public class QuadExpansionTests
{
    private const float Tolerance = 0.01f;

    [Fact]
    public void Factor1_ReturnsTheExactSameQuad()
    {
        // Deliberately NOT a perfect parallelogram (TR-TL slightly longer
        // than BR-BL) -- a real ContourCardDetector quad from a keystoned
        // photograph never is one exactly. If Expand ever routed this
        // through the parallelogram-decomposition math even at (1, 1), it
        // would SYMMETRIZE the quad and silently move a corner -- exactly
        // the bug this test caught during E1a's own experiment run (the
        // "expanded" column differed from "baseline" at factor 1.0, which
        // should be impossible). Asserting exact equality, not "close",
        // pins that it cannot recur.
        var quad = new CardQuad(
            TL: new PointF2(10, 20),
            TR: new PointF2(210, 18), // 2px higher than a perfect rectangle would put it
            BR: new PointF2(208, 300),
            BL: new PointF2(12, 302));

        var result = QuadExpansion.Expand(quad, 1f, 1f);

        Assert.Equal(quad, result);
    }

    [Fact]
    public void AxisAlignedRectangle_ExpandsSymmetricallyAboutItsCentroid()
    {
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(100, 0),
            BR: new PointF2(100, 200),
            BL: new PointF2(0, 200));

        var result = QuadExpansion.Expand(quad, widthFactor: 1.2f, heightFactor: 1.5f);

        // Width 100 -> 120 (centred on x=50, so -10..110); height 200 -> 300
        // (centred on y=100, so -50..250).
        AssertPoint(new PointF2(-10, -50), result.TL);
        AssertPoint(new PointF2(110, -50), result.TR);
        AssertPoint(new PointF2(110, 250), result.BR);
        AssertPoint(new PointF2(-10, 250), result.BL);
    }

    [Fact]
    public void Shrinking_MovesCornersInwardTowardTheCentroid()
    {
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(100, 0),
            BR: new PointF2(100, 200),
            BL: new PointF2(0, 200));

        var result = QuadExpansion.Expand(quad, widthFactor: 0.5f, heightFactor: 0.5f);

        // Half size, same centroid (50, 100): 25..75 x 50..150.
        AssertPoint(new PointF2(25, 50), result.TL);
        AssertPoint(new PointF2(75, 50), result.TR);
        AssertPoint(new PointF2(75, 150), result.BR);
        AssertPoint(new PointF2(25, 150), result.BL);
    }

    /// A PORTRAIT-oriented quad (rotated 90 degrees versus the axis-aligned
    /// case above) is exactly the shape the 3x3-grid frames in this
    /// package's own corpus contain. Scaling "width" and "height" must
    /// still track the QUAD's own axes, not the frame's -- i.e. this test
    /// would fail if `Expand` ever scaled X/Y in frame coordinates instead
    /// of the quad's own TL-TR/TL-BL edge directions.
    [Fact]
    public void RotatedQuad_ExpandsAlongItsOwnAxes_NotFrameAxes()
    {
        // A quad whose "width" edge (TL-TR) runs mostly VERTICALLY in frame
        // coordinates and whose "height" edge (TL-BL) runs mostly
        // HORIZONTALLY -- i.e. rotated ~90 degrees from the axis-aligned
        // case.
        var quad = new CardQuad(
            TL: new PointF2(100, 0),
            TR: new PointF2(100, 200), // "width" edge points straight down
            BR: new PointF2(0, 200),
            BL: new PointF2(0, 0)); // "height" edge points straight left

        var result = QuadExpansion.Expand(quad, widthFactor: 2f, heightFactor: 1f);

        // The "width" axis (TL-TR / BL-BR: (0,200), magnitude 200, pointing
        // in +Y) must double to magnitude 400; the "height" axis (TL-BL /
        // TR-BR: (-100,0), magnitude 100, pointing in -X) must stay the
        // same. Centroid is (50, 100), so the vertical span becomes
        // -100..300 and the horizontal span stays 0..100.
        AssertPoint(new PointF2(100, -100), result.TL);
        AssertPoint(new PointF2(100, 300), result.TR);
        AssertPoint(new PointF2(0, 300), result.BR);
        AssertPoint(new PointF2(0, -100), result.BL);
    }

    [Fact]
    public void WidthAndHeightFactors_AreIndependent()
    {
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(100, 0),
            BR: new PointF2(100, 200),
            BL: new PointF2(0, 200));

        var widthOnly = QuadExpansion.Expand(quad, 2f, 1f);
        var heightOnly = QuadExpansion.Expand(quad, 1f, 2f);

        // Width-only: X span doubles (0..100 -> -50..150), Y span untouched.
        AssertPoint(new PointF2(-50, 0), widthOnly.TL);
        AssertPoint(new PointF2(150, 0), widthOnly.TR);
        AssertPoint(new PointF2(0, -100), heightOnly.TL); // X untouched, Y span doubled
        AssertPoint(new PointF2(100, 300), heightOnly.BR); // Y span doubles (0..200 -> -100..300)
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(1f, -0.5f)]
    public void NonPositiveFactor_Throws(float widthFactor, float heightFactor)
    {
        var quad = new CardQuad(new PointF2(0, 0), new PointF2(10, 0), new PointF2(10, 10), new PointF2(0, 10));

        Assert.Throws<ArgumentOutOfRangeException>(() => QuadExpansion.Expand(quad, widthFactor, heightFactor));
    }

    [Fact]
    public void CentroidIsPreserved_AcrossAnyFactor()
    {
        var quad = new CardQuad(
            TL: new PointF2(15, 40),
            TR: new PointF2(215, 35),
            BR: new PointF2(210, 320),
            BL: new PointF2(20, 325));

        var originalCentroid = Centroid(quad);
        var result = QuadExpansion.Expand(quad, 1.3f, 0.7f);
        var resultCentroid = Centroid(result);

        AssertPoint(originalCentroid, resultCentroid);
    }

    private static PointF2 Centroid(CardQuad q) =>
        new((q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f, (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    private static void AssertPoint(PointF2 expected, PointF2 actual)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) < Tolerance && Math.Abs(expected.Y - actual.Y) < Tolerance,
            $"Expected ({expected.X}, {expected.Y}), got ({actual.X}, {actual.Y}).");
    }
}
