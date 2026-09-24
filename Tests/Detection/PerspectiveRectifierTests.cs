using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Detection;

/// `PerspectiveRectifier` (package B5b): CardSpotter step 1, query side.
/// Pins three things that would otherwise degrade silently, per CLAUDE.md
/// "The one gate that matters most": the interpolation flag (must be
/// `INTER_LINEAR` -- `warpPerspective` cannot do `INTER_AREA` at all), the
/// pixel-centre destination convention (`PerspectiveRectifier`'s own doc
/// comment), and corner-ordering fidelity (a swapped destination pair would
/// mirror the card; a relabeled-180 source quad must rotate it, never
/// mirror it).
public class PerspectiveRectifierTests
{
    private readonly ITestOutputHelper _output;

    public PerspectiveRectifierTests(ITestOutputHelper output) => _output = output;

    // Four maximally-distinguishable BGR marker colors, pure enough that a
    // few px of INTER_LINEAR blending at their own centers still reads back
    // unambiguously as "this marker" rather than a blend of two markers.
    private static readonly Scalar Red = new(0, 0, 255);
    private static readonly Scalar Green = new(0, 255, 0);
    private static readonly Scalar Blue = new(255, 0, 0);
    private static readonly Scalar Yellow = new(0, 255, 255);

    [Fact]
    public void Rectify_KnownQuad_PlacesMarkersAtExpectedCornerPixels_CorrectSizeAndStride()
    {
        var quad = new CardQuad(
            TL: new PointF2(60, 70),
            TR: new PointF2(360, 45),
            BR: new PointF2(380, 470),
            BL: new PointF2(40, 490));

        using var frame = MakeMarkerFrame(700, 700, quad);

        var rectifier = new PerspectiveRectifier();
        var card = rectifier.Rectify(frame, quad);

        Assert.Equal(RectifiedCard.CanonicalWidth * 3, card.Stride); // Bgr24 in, Bgr24 out
        Assert.Equal(PixelLayout.Bgr24, card.Layout);
        Assert.Equal(RectifiedCard.CanonicalHeight * card.Stride, card.Pixels.Length);
        Assert.Equal(quad, card.SourceQuad);

        using var mat = QueryTransform.ToMat(card);
        AssertPixelNear(mat, 0, 0, Red, "TL -> (0,0)");
        AssertPixelNear(mat, RectifiedCard.CanonicalWidth - 1, 0, Green, "TR -> (487,0)");
        AssertPixelNear(mat, RectifiedCard.CanonicalWidth - 1, RectifiedCard.CanonicalHeight - 1, Blue, "BR -> (487,679)");
        AssertPixelNear(mat, 0, RectifiedCard.CanonicalHeight - 1, Yellow, "BL -> (0,679)");
    }

    [Fact]
    public void Rectify_AxisAlignedExactSizeQuad_IsANearIdentityCopy()
    {
        using var source = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 11);
        using var frame = FrameMat.FromMat(source);

        // Exactly the pixel-centre convention PerspectiveRectifier itself
        // uses for its destination points -- so this quad IS the identity
        // mapping, and the warp should reproduce the source almost exactly.
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(RectifiedCard.CanonicalWidth - 1, 0),
            BR: new PointF2(RectifiedCard.CanonicalWidth - 1, RectifiedCard.CanonicalHeight - 1),
            BL: new PointF2(0, RectifiedCard.CanonicalHeight - 1));

        var rectifier = new PerspectiveRectifier();
        var card = rectifier.Rectify(frame, quad);

        using var rectifiedMat = QueryTransform.ToMat(card);
        using var diff = new Mat();
        Cv2.Absdiff(source, rectifiedMat, diff);
        using var grayDiff = new Mat();
        Cv2.CvtColor(diff, grayDiff, ColorConversionCodes.BGR2GRAY);
        var meanDiff = Cv2.Mean(grayDiff).Val0;

        _output.WriteLine($"identity-warp mean abs diff = {meanDiff:F3}");
        Assert.InRange(meanDiff, 0, 1.0);
    }

    [Fact]
    public void Rectify_180DegreeRelabeledQuad_RotatesTheOutput_NeverMirrorsIt()
    {
        var quad = new CardQuad(
            TL: new PointF2(60, 70),
            TR: new PointF2(360, 45),
            BR: new PointF2(380, 470),
            BL: new PointF2(40, 490));

        using var frame = MakeMarkerFrame(700, 700, quad);

        // A quad whose corner LABELS are rotated 180 degrees from the true
        // orientation -- what a detector would hand back if it read this
        // same physical card upside down (CLAUDE.md "Card orientation is
        // unhandled": a quad's TL/TR/BR/BL are geometric labels, not a
        // semantic "this edge is the top", so this is a legal CardQuad).
        var relabeled = new CardQuad(TL: quad.BR, TR: quad.BL, BR: quad.TL, BL: quad.TR);

        var rectifier = new PerspectiveRectifier();
        var rotatedCard = rectifier.Rectify(frame, relabeled);

        using var rotatedMat = QueryTransform.ToMat(rotatedCard);

        // 180-degree rotation: what was at TL now shows at BR and vice
        // versa. NOT a mirror -- a horizontal flip would instead swap
        // TL<->TR and BL<->BR, which is a different, distinguishable
        // outcome (asserted explicitly below).
        AssertPixelNear(rotatedMat, 0, 0, Blue, "relabeled TL (=true BR) -> (0,0)");
        AssertPixelNear(rotatedMat, RectifiedCard.CanonicalWidth - 1, 0, Yellow, "relabeled TR (=true BL) -> (487,0)");
        AssertPixelNear(rotatedMat, RectifiedCard.CanonicalWidth - 1, RectifiedCard.CanonicalHeight - 1, Red, "relabeled BR (=true TL) -> (487,679)");
        AssertPixelNear(rotatedMat, 0, RectifiedCard.CanonicalHeight - 1, Green, "relabeled BL (=true TR) -> (0,679)");

        // Explicitly rule out the mirror reading: a horizontal flip of the
        // correct (non-relabeled) result would put GREEN (not blue) at
        // (0,0) -- confirm that is NOT what we got.
        var (b, g, r) = SamplePixel(rotatedMat, 0, 0);
        var isGreenish = g > b && g > r;
        Assert.False(isGreenish, "output at (0,0) reads as a mirror (green, the TR marker) rather than a 180-degree rotation (blue, the BR marker).");
    }

    [Fact]
    public void Rectify_UsesLinearInterpolation_SubPixelEdgeProducesIntermediateGrey()
    {
        // A hard vertical black/white edge, with the destination quad
        // shifted by exactly +0.5px relative to the pixel-centre identity
        // mapping -- so sampling destination column 243 lands EXACTLY
        // halfway between source columns 243 (black) and 244 (white).
        // INTER_LINEAR must blend that to an intermediate grey; INTER_
        // NEAREST would snap to one side or the other and never produce a
        // mid-value here. This is the regression test for chaos case (a).
        const int width = RectifiedCard.CanonicalWidth;
        const int height = RectifiedCard.CanonicalHeight;
        using var edge = new Mat(height, width, MatType.CV_8UC3, Scalar.All(0));
        using (var rightHalf = new Mat(edge, new Rect(width / 2, 0, width - (width / 2), height)))
        {
            rightHalf.SetTo(Scalar.All(255));
        }

        using var frame = FrameMat.FromMat(edge);

        var quad = new CardQuad(
            TL: new PointF2(0.5f, 0),
            TR: new PointF2(width - 0.5f, 0),
            BR: new PointF2(width - 0.5f, height - 1),
            BL: new PointF2(0.5f, height - 1));

        var rectifier = new PerspectiveRectifier();
        var card = rectifier.Rectify(frame, quad);

        using var mat = QueryTransform.ToMat(card);
        var (b, g, r) = SamplePixel(mat, (width / 2) - 1, height / 2);

        _output.WriteLine($"sub-pixel edge sample (B,G,R) = ({b},{g},{r})");
        Assert.InRange((int)b, 60, 195);
        Assert.Equal(b, g);
        Assert.Equal(g, r);
    }

    [Fact]
    public void Rectify_RoundTrip_DetectedAndRectifiedCardIsCloseToOriginal_AndHashesNearby()
    {
        // A synthetic, structurally card-like image (black border framing a
        // high-frequency gradient interior -- ContourCardDetector needs the
        // border's contrast to find an edge at all; CardHasher needs the
        // interior's high-frequency content for its per-cell median to be
        // meaningful) projected into a larger frame with a MILD keystone
        // (the four corners are not a perfect axis-aligned rectangle) on a
        // contrasting mat background, run through the shipping
        // detect -> rectify -> hash path exactly as the scanner would.
        using var originalCard = MakeBorderedSyntheticCard(seed: 21);

        const int frameWidth = 760;
        const int frameHeight = 900;
        var targetQuad = new CardQuad(
            TL: new PointF2(190, 210),
            TR: new PointF2(560, 195),
            BR: new PointF2(575, 715),
            BL: new PointF2(175, 705));

        using var frame = ProjectCardIntoMat(originalCard, frameWidth, frameHeight, targetQuad);

        var detector = new ContourCardDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContourCardDetector>.Instance);
        var detected = detector.Detect(frame, maxCards: 1);
        Assert.Single(detected);

        var rectifier = new PerspectiveRectifier();
        var rectified = rectifier.Rectify(frame, detected[0]);

        Assert.Equal(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalWidth);

        using var rectifiedMat = QueryTransform.ToMat(rectified);
        using var rectifiedFlipped = new Mat();
        Cv2.Flip(rectifiedMat, rectifiedFlipped, FlipMode.XY);

        // Orientation is genuinely ambiguous here (CLAUDE.md "Card
        // orientation is unhandled" -- corner ordering recovers the true
        // quad OR its 180-degree rotation, never a 90-degree rotation or a
        // mirror), so compare against whichever of upright/flipped is
        // closer, exactly as HashCardIdentifier does for the real query
        // path (see its own "hash both orientations" doc comment).
        var meanDiffUpright = MeanAbsDiff(originalCard, rectifiedMat);
        var meanDiffFlipped = MeanAbsDiff(originalCard, rectifiedFlipped);
        var meanDiff = Math.Min(meanDiffUpright, meanDiffFlipped);

        _output.WriteLine($"round-trip mean abs diff (best of upright/flipped) = {meanDiff:F2}");
        Assert.InRange(meanDiff, 0, 20.0);

        using var grayOriginal = ReferenceTransform.Prepare(originalCard);
        var originalHash = CardHasher.Hash(grayOriginal);

        using var grayQueryUpright = QueryTransform.Prepare(rectified);
        var queryHashUpright = CardHasher.Hash(grayQueryUpright);

        using var grayQueryFlippedMat = new Mat();
        Cv2.Flip(grayQueryUpright, grayQueryFlippedMat, FlipMode.XY);
        var queryHashFlipped = CardHasher.Hash(grayQueryFlippedMat);

        var distance = Math.Min(
            originalHash.HammingDistance(queryHashUpright),
            originalHash.HammingDistance(queryHashFlipped));

        _output.WriteLine($"round-trip Hamming distance (best of upright/flipped) = {distance}");
        Assert.InRange(distance, 0, 220);
    }

    [Fact]
    public void Rectify_NullFrame_Throws()
    {
        var rectifier = new PerspectiveRectifier();
        var quad = new CardQuad(new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1));
        Assert.Throws<ArgumentNullException>(() => rectifier.Rectify(null!, quad));
    }

    private static double MeanAbsDiff(Mat a, Mat b)
    {
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        using var grayDiff = new Mat();
        Cv2.CvtColor(diff, grayDiff, ColorConversionCodes.BGR2GRAY);
        return Cv2.Mean(grayDiff).Val0;
    }

    /// A black-bordered card: a solid dark frame around a high-frequency
    /// gradient interior, at the canonical 488x680 size. The border is what
    /// gives `ContourCardDetector` an edge to find (a borderless gradient
    /// has no reliable Canny edge at its own boundary); the interior is
    /// what gives `CardHasher`'s per-cell median genuine content to react
    /// to, matching `SyntheticImages.MakeCardLikeBgr`'s own rationale.
    private static Mat MakeBorderedSyntheticCard(int seed)
    {
        const int width = RectifiedCard.CanonicalWidth;
        const int height = RectifiedCard.CanonicalHeight;
        const int borderPx = 24;

        var card = new Mat(height, width, MatType.CV_8UC3, new Scalar(12, 12, 12));
        using var interior = SyntheticImages.MakeCardLikeBgr(width - (2 * borderPx), height - (2 * borderPx), seed);

        using (var interiorRegion = new Mat(card, new Rect(borderPx, borderPx, width - (2 * borderPx), height - (2 * borderPx))))
        {
            interior.CopyTo(interiorRegion);
        }

        return card;
    }

    /// Warps `card` (assumed 488x680) into `targetQuad`'s position inside a
    /// `frameWidth`x`frameHeight` mat background, via `BORDER_TRANSPARENT`
    /// so only the pixels the forward mapping's inverse actually reaches
    /// within the source card are overwritten -- everywhere else keeps the
    /// pre-painted background, giving a realistic "card sitting on a mat"
    /// scene for `ContourCardDetector` to run against.
    private static CameraFrame ProjectCardIntoMat(Mat card, int frameWidth, int frameHeight, CardQuad targetQuad)
    {
        using var canvas = new Mat(frameHeight, frameWidth, MatType.CV_8UC3, Scalar.All(170));
        using var noise = new Mat(canvas.Size(), canvas.Type());
        Cv2.Randu(noise, Scalar.All(-6), Scalar.All(6));
        Cv2.Add(canvas, noise, canvas);

        var srcPoints = new[]
        {
            new Point2f(0, 0),
            new Point2f(card.Cols - 1, 0),
            new Point2f(card.Cols - 1, card.Rows - 1),
            new Point2f(0, card.Rows - 1),
        };
        var dstPoints = new[]
        {
            new Point2f(targetQuad.TL.X, targetQuad.TL.Y),
            new Point2f(targetQuad.TR.X, targetQuad.TR.Y),
            new Point2f(targetQuad.BR.X, targetQuad.BR.Y),
            new Point2f(targetQuad.BL.X, targetQuad.BL.Y),
        };

        using var transform = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
        Cv2.WarpPerspective(
            card, canvas, transform, canvas.Size(),
            InterpolationFlags.Linear, BorderTypes.Transparent);

        return FrameMat.FromMat(canvas);
    }

    /// Builds a frame with four small, pure-colored square markers centered
    /// exactly at `quad`'s own four corners (Red@TL, Green@TR, Blue@BR,
    /// Yellow@BL) against a neutral mid-gray background -- the fixture
    /// `Rectify_KnownQuad_...` and `Rectify_180DegreeRelabeledQuad_...` both
    /// use to check exactly which source pixel a given output pixel came
    /// from.
    private static CameraFrame MakeMarkerFrame(int width, int height, CardQuad quad)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(120));
        DrawMarker(mat, quad.TL, Red);
        DrawMarker(mat, quad.TR, Green);
        DrawMarker(mat, quad.BR, Blue);
        DrawMarker(mat, quad.BL, Yellow);
        return FrameMat.FromMat(mat);
    }

    private static void DrawMarker(Mat mat, PointF2 center, Scalar color)
    {
        const int halfSize = 8;
        var x = (int)MathF.Round(center.X);
        var y = (int)MathF.Round(center.Y);
        Cv2.Rectangle(
            mat,
            new Point(x - halfSize, y - halfSize),
            new Point(x + halfSize, y + halfSize),
            color,
            thickness: -1);
    }

    private static (byte B, byte G, byte R) SamplePixel(Mat bgr, int x, int y)
    {
        var vec = bgr.At<Vec3b>(y, x);
        return (vec.Item0, vec.Item1, vec.Item2);
    }

    private static void AssertPixelNear(Mat bgr, int x, int y, Scalar expected, string label)
    {
        var (b, g, r) = SamplePixel(bgr, x, y);
        const int tolerance = 40; // pure marker fill vs. a few px of LINEAR blending at the exact center
        Assert.True(
            Math.Abs(b - expected.Val0) <= tolerance && Math.Abs(g - expected.Val1) <= tolerance && Math.Abs(r - expected.Val2) <= tolerance,
            $"{label}: expected BGR~=({expected.Val0},{expected.Val1},{expected.Val2}), got ({b},{g},{r}).");
    }
}
