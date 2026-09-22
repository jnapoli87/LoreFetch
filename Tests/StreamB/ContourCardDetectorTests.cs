using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Package B5a: `ContourCardDetector` on generated frames (CI has no real
/// imagery -- CLAUDE.md "Never commit card imagery"). See
/// `DetectorTestFrames` for the scene generators and
/// `ContourCardDetectorRealCaptureTests` for the artifact-gated real-C920
/// check.
public class ContourCardDetectorTests
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;

    private static ContourCardDetector NewDetector() => new(NullLogger<ContourCardDetector>.Instance);

    [Theory]
    [InlineData((byte)220, false)] // light mat
    [InlineData((byte)130, false)] // mid mat
    [InlineData((byte)30, false)] // dark mat -- Risk 4
    [InlineData((byte)30, true)] // dark mat with a seam line
    public void Detect_EmptyMat_ReturnsZero(byte brightness, bool withSeam)
    {
        using var frame = DetectorTestFrames.EmptyMat(FrameWidth, FrameHeight, brightness, seed: 42, withSeam);
        var detector = NewDetector();

        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Empty(quads);
    }

    [Fact]
    public void Detect_SingleCard_ReturnsOne()
    {
        var (frame, _) = DetectorTestFrames.CardOnMat(FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 160, angleDegrees: 0);
        using var f = frame;
        var detector = NewDetector();

        var quads = detector.Detect(f, maxCards: 9);

        Assert.Single(quads);
    }

    [Fact]
    public void Detect_ThreeInALine_ReturnsThree()
    {
        using var frame = DetectorTestFrames.CardGrid(960, 400, rows: 1, cols: 3, count: 3);
        var detector = NewDetector();

        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Equal(3, quads.Count);
    }

    [Fact]
    public void Detect_PartialNineGrid_SevenOfNine_ReturnsSeven()
    {
        using var frame = DetectorTestFrames.CardGrid(960, 960, rows: 3, cols: 3, count: 7);
        var detector = NewDetector();

        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Equal(7, quads.Count);
    }

    [Fact]
    public void Detect_HandBlob_RejectsAsNonQuad()
    {
        using var frame = DetectorTestFrames.HandBlob(FrameWidth, FrameHeight);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.Empty(diagnostics.Accepted);
    }

    [Fact]
    public void Detect_Square_RejectedByAspect()
    {
        using var frame = DetectorTestFrames.Square(FrameWidth, FrameHeight, sidePx: 160);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.Empty(diagnostics.Accepted);
        Assert.Contains(diagnostics.Rejected, r => r.Reason == ContourRejectReason.AspectRatio);
    }

    [Fact]
    public void Detect_TinyCardShapedSpeck_RejectedByMinArea()
    {
        // Correct aspect ratio, far too small to be a real card -- this is
        // the one case the aspect filter alone cannot catch, so it isolates
        // the minimum-area filter specifically.
        using var frame = DetectorTestFrames.TinyCardShapedSpeck(FrameWidth, FrameHeight, shortSidePx: 20);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.Empty(diagnostics.Accepted);
        Assert.Contains(diagnostics.Rejected, r => r.Reason == ContourRejectReason.MinArea);
    }

    [Fact]
    public void Detect_ThinRectangle_RejectedByAspect()
    {
        using var frame = DetectorTestFrames.ThinRectangle(FrameWidth, FrameHeight, shortSidePx: 30, longSidePx: 300);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.Empty(diagnostics.Accepted);
        Assert.Contains(diagnostics.Rejected, r => r.Reason == ContourRejectReason.AspectRatio);
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(90f)] // "landscape" -- long axis horizontal in-frame
    public void Detect_RotatedCard_CornersOrderedCorrectly(float angleDegrees)
    {
        var (frame, groundTruth) = DetectorTestFrames.CardOnMat(
            FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 140, angleDegrees);
        using var f = frame;
        var detector = NewDetector();

        var quads = detector.Detect(f, maxCards: 9);

        var quad = Assert.Single(quads);
        AssertOrderedUpToHalfTurn(groundTruth, quad, toleranceOfPx: 10f);
    }

    [Fact]
    public void Detect_CardTouchingBorder_Rejected()
    {
        using var frame = DetectorTestFrames.CardTouchingBorder(FrameWidth, FrameHeight, shortSidePx: 140);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.Empty(diagnostics.Accepted);
        Assert.Contains(diagnostics.Rejected, r => r.Reason == ContourRejectReason.TouchesBorder);
    }

    [Fact]
    public void Detect_MaxCardsHonoured_DescendingArea()
    {
        // Four cards of DELIBERATELY DIFFERENT sizes -- same-size cards
        // (e.g. a uniform grid) can't distinguish ascending from descending
        // order, since every area ties; distinct sizes make the ordering
        // observable, which is the whole point of this test.
        using var frame = DetectorTestFrames.CardsOfDifferentSizes(
            960,
            960,
            [(160f, 160f, 60f), (500f, 200f, 90f), (200f, 600f, 120f), (600f, 650f, 150f)]);
        var detector = NewDetector();

        var quads = detector.Detect(frame, maxCards: 3);

        Assert.Equal(3, quads.Count);
        for (var i = 1; i < quads.Count; i++)
        {
            Assert.True(quads[i - 1].AreaPx > quads[i].AreaPx, "Expected strictly descending area.");
        }

        // The smallest of the four (short side 60) must have been dropped
        // by the maxCards=3 cap, not merely sorted last -- otherwise an
        // ascending-order bug that also silently drops the wrong end (the
        // largest) could still pass the descending check above.
        Assert.DoesNotContain(quads, q => q.AreaPx < 5000);
    }

    [Fact]
    public void Detect_NestedInnerFrameBand_DoesNotDoubleCount()
    {
        // DetectorTestFrames.CardOnMat draws a full card: outer border,
        // inner frame band and art box. If nested/duplicate dedupe were
        // broken, the inner frame band -- which shares the card's own
        // aspect family -- could itself pass every filter and be counted
        // as a second card. `NewDetector()` uses `ContourDetectorOptions
        // .Default`, which is now `RetrievalModes.List` -- under the OLD
        // `External` default this scenario never reached the dedupe
        // branch at all (the inner contour never reached `findContours`'s
        // output), so this test now exercises `DedupeAndTakeTopN`'s
        // nested-duplicate suppression for real, end-to-end, for the
        // first time; see `ContourCardDetectorRetrievalModeTests` for the
        // same scenario asserted more explicitly under both modes.
        var (frame, _) = DetectorTestFrames.CardOnMat(FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 200, angleDegrees: 0);
        using var f = frame;
        var detector = NewDetector();

        var quads = detector.Detect(f, maxCards: 9);

        Assert.Single(quads);
    }

    /// Direct test of `ContourCardDetector.DedupeAndTakeTopN` (internal,
    /// exposed via `InternalsVisibleTo`), fed two hand-built overlapping
    /// quads rather than routed through Canny/`findContours`. This isolates
    /// the dedupe method's own logic from detection geometry, independent
    /// of which retrieval mode is active -- valuable in its own right, not
    /// just as a workaround.
    ///
    /// Historical note: this test predates the switch to `RetrievalModes
    /// .List` as the shipped default. At the time it was written, it was
    /// also the ONLY way to exercise this branch at all: under the OLD
    /// `External` default, `findContours` structurally excludes a
    /// genuinely nested contour from its own output before this code ever
    /// sees it -- three synthetic concentric-quad constructions and all 12
    /// real `test-images/ad-hoc/` captures produced zero `NestedDuplicate`
    /// rejections under `External`. Under the current `List` default that
    /// is no longer true (see `Detect_NestedInnerFrameBand_DoesNotDoubleCount`
    /// above, and `ContourCardDetectorRetrievalModeTests`, both of which now
    /// exercise this branch end-to-end through Canny), but this direct unit
    /// test is kept: calling the real method with a realistic input (a
    /// small quad whose centroid sits inside a larger one, exactly what a
    /// mis-detected inner frame/art-box border would look like) still
    /// targets this package's chaos case (e) more directly than an
    /// end-to-end image does.
    [Fact]
    public void DedupeAndTakeTopN_SmallQuadCentroidInsideLargerQuad_DropsTheSmallerOne()
    {
        var large = new CardQuad(
            TL: new PointF2(100, 100), TR: new PointF2(300, 100), BR: new PointF2(300, 400), BL: new PointF2(100, 400));
        var small = new CardQuad(
            TL: new PointF2(150, 150), TR: new PointF2(250, 150), BR: new PointF2(250, 350), BL: new PointF2(150, 350));
        var rejected = new List<RejectedContour>();

        var result = ContourCardDetector.DedupeAndTakeTopN([large, small], maxCards: 9, rejected);

        var kept = Assert.Single(result);
        Assert.Equal(large, kept);
        Assert.Contains(rejected, r => r.Reason == ContourRejectReason.NestedDuplicate);
    }

    [Fact]
    public void DetectWithDiagnostics_EveryRejectionCarriesAReason()
    {
        using var frame = DetectorTestFrames.Square(FrameWidth, FrameHeight, sidePx: 160);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.NotEmpty(diagnostics.Rejected);
        Assert.All(diagnostics.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Detail)));
    }

    [Fact]
    public void DetectWithDiagnostics_AspectJustOutsideStrictTolerance_WidensAndAccepts()
    {
        // Target aspect is 1.3968 +-15% -> [1.187, 1.606]. 1.75 sits outside
        // that but inside the +-25% widened band [1.048, 1.746]... make it
        // 1.74 so it's unambiguously inside the widened band and outside
        // the strict one.
        const float shortSidePx = 150f;
        var longSidePx = shortSidePx * 1.74f;

        var frame = DetectorTestFrames.ThinRectangle(FrameWidth, FrameHeight, shortSidePx, longSidePx);
        using var f = frame;
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(f, maxCards: 9);

        Assert.True(diagnostics.UsedWidenedAspectTolerance);
        var quad = Assert.Single(diagnostics.Accepted);
        Assert.InRange(quad.AspectRatio, 1.6f, 1.9f);
    }

    [Fact]
    public void DetectWithDiagnostics_AspectFarOutsideEvenWidenedTolerance_NeverWidens()
    {
        using var frame = DetectorTestFrames.Square(FrameWidth, FrameHeight, sidePx: 160);
        var detector = NewDetector();

        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        Assert.False(diagnostics.UsedWidenedAspectTolerance);
        Assert.Empty(diagnostics.Accepted);
    }

    [Fact]
    public void Detect_ZeroMaxCards_ReturnsEmptyWithoutThrowing()
    {
        var (frame, _) = DetectorTestFrames.CardOnMat(FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 160, angleDegrees: 0);
        using var f = frame;
        var detector = NewDetector();

        var quads = detector.Detect(f, maxCards: 0);

        Assert.Empty(quads);
    }

    /// Accepts either the true orientation or its 180-degree relabeling
    /// (CLAUDE.md: "card orientation is unhandled" -- upside-down is a
    /// separate, accepted problem solved by hashing both orientations, not
    /// by the detector), each corner within `toleranceOfPx`. Never accepts
    /// a 90-degree rotation or a mirror -- see `ContourCardDetector`'s own
    /// doc comment for why those two are excluded by construction.
    private static void AssertOrderedUpToHalfTurn(CardQuad expected, CardQuad actual, float toleranceOfPx)
    {
        var sameOrientation =
            Close(expected.TL, actual.TL, toleranceOfPx) &&
            Close(expected.TR, actual.TR, toleranceOfPx) &&
            Close(expected.BR, actual.BR, toleranceOfPx) &&
            Close(expected.BL, actual.BL, toleranceOfPx);

        var halfTurn =
            Close(expected.TL, actual.BR, toleranceOfPx) &&
            Close(expected.TR, actual.BL, toleranceOfPx) &&
            Close(expected.BR, actual.TL, toleranceOfPx) &&
            Close(expected.BL, actual.TR, toleranceOfPx);

        Assert.True(
            sameOrientation || halfTurn,
            $"Expected {expected} (or its 180-degree relabeling) within {toleranceOfPx}px, got {actual}.");
    }

    private static bool Close(PointF2 a, PointF2 b, float toleranceOfPx)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy)) <= toleranceOfPx;
    }
}
