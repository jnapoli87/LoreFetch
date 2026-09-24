using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using OpenCvSharp;

namespace LoreFetch.Tests.Support;

/// Deterministic, code-generated frames for `ContourCardDetectorTests` --
/// never a file on disk, so there is no card-imagery risk (CLAUDE.md
/// "Never commit card imagery"). Complements `SyntheticImages` (which
/// targets the hash pipeline) with frames shaped for the DETECTOR: whole
/// scenes with a background/mat, rather than a single card's own pixels.
internal static class DetectorTestFrames
{
    // 63:88 mm real card proportions -- long/short = 1.3968.
    public const float CardAspect = 88f / 63f;

    /// A plain mat background at the given brightness, with per-pixel noise
    /// and an optional straight "seam" line (simulating a mat's stitched
    /// edge) -- no card-shaped content anywhere, so a correct detector must
    /// return zero quads.
    public static CameraFrame EmptyMat(int width, int height, byte brightness, int seed, bool withSeam = false)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(brightness));
        AddNoise(mat, seed, amplitude: 10);

        if (withSeam)
        {
            var y = height / 2;
            Cv2.Line(mat, new Point(0, y), new Point(width - 1, y), new Scalar(brightness - 20, brightness - 20, brightness - 20), 2);
        }

        return FrameMat.FromMat(mat);
    }

    /// One rotated, structurally card-like rectangle (black border, lighter
    /// inner frame, a differently-proportioned art box) painted onto a
    /// contrasting mat background. `centerX`/`centerY`/`shortSidePx` place
    /// it; `angleDegrees` rotates it about its own center (0 = short edges
    /// horizontal, i.e. a portrait card standing upright; 90 = "landscape"
    /// -- long edges horizontal). Returns the frame plus the OUTER quad's
    /// ground-truth corners, unrotated-local-frame labelled TL/TR/BR/BL,
    /// for `ContourCardDetectorTests` to check ordering against.
    public static (CameraFrame Frame, CardQuad GroundTruth) CardOnMat(
        int width,
        int height,
        float centerX,
        float centerY,
        float shortSidePx,
        float angleDegrees,
        byte matBrightness = 170,
        int seed = 1)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        var longSidePx = shortSidePx * CardAspect;
        var groundTruth = DrawCard(mat, centerX, centerY, shortSidePx, longSidePx, angleDegrees);

        return (FrameMat.FromMat(mat), groundTruth);
    }

    /// A row of `count` upright, non-overlapping cards, evenly spaced --
    /// used for the "3 in a line" and "partial 3x3 grid" acceptance cases
    /// (a 3x3 grid is just a 2D arrangement; a generic grid helper covers
    /// both by choosing rows/cols).
    public static CameraFrame CardGrid(int width, int height, int rows, int cols, int count, byte matBrightness = 170, int seed = 2)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        const float marginFraction = 0.08f;
        const float gutterFraction = 0.12f;

        var marginX = width * marginFraction;
        var marginY = height * marginFraction;
        var cellWidth = (width - (2 * marginX)) / cols;
        var cellHeight = (height - (2 * marginY)) / rows;

        var cardWidth = cellWidth * (1f - gutterFraction);
        var cardHeight = cardWidth * CardAspect;
        if (cardHeight > cellHeight * (1f - gutterFraction))
        {
            cardHeight = cellHeight * (1f - gutterFraction);
            cardWidth = cardHeight / CardAspect;
        }

        var placed = 0;
        for (var row = 0; row < rows && placed < count; row++)
        {
            for (var col = 0; col < cols && placed < count; col++)
            {
                var cx = marginX + ((col + 0.5f) * cellWidth);
                var cy = marginY + ((row + 0.5f) * cellHeight);
                DrawCard(mat, cx, cy, cardWidth, cardHeight, angleDegrees: 0, seedOffset: placed);
                placed++;
            }
        }

        return FrameMat.FromMat(mat);
    }

    /// Several cards of DELIBERATELY DIFFERENT sizes, placed far enough
    /// apart not to touch -- unlike `CardGrid` (same-size cards, where
    /// ascending and descending area orderings are indistinguishable),
    /// this is what actually exercises "top N by DESCENDING area": a
    /// mutation that returns ascending order instead only shows up when
    /// the areas genuinely differ.
    public static CameraFrame CardsOfDifferentSizes(
        int width, int height, IReadOnlyList<(float CenterX, float CenterY, float ShortSidePx)> placements, byte matBrightness = 170, int seed = 8)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        foreach (var (cx, cy, shortSidePx) in placements)
        {
            DrawCard(mat, cx, cy, shortSidePx, shortSidePx * CardAspect, angleDegrees: 0);
        }

        return FrameMat.FromMat(mat);
    }

    /// A rounded, non-rectangular skin-toned blob standing in for a hand --
    /// an ellipse plus a couple of overlapping smaller ellipses (fingers),
    /// which `approxPolyDP` should never collapse to a clean 4-point
    /// convex quad the way a card's straight edges do.
    public static CameraFrame HandBlob(int width, int height, byte matBrightness = 170, int seed = 3)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        var skin = new Scalar(120, 170, 210); // BGR -- a plausible skin tone
        var palmCenter = new Point(width / 2, height / 2);
        var palmAxes = new Size(width / 8, height / 6);
        Cv2.Ellipse(mat, palmCenter, palmAxes, 0, 0, 360, skin, thickness: -1);

        var rng = new Random(seed);
        for (var finger = 0; finger < 4; finger++)
        {
            var angle = -60 + (finger * 40);
            var rad = angle * Math.PI / 180.0;
            var fx = palmCenter.X + (int)(palmAxes.Width * 1.6 * Math.Cos(rad));
            var fy = palmCenter.Y - (int)(palmAxes.Height * 2.2 * Math.Sin(rad));
            var fingerAxes = new Size(width / 40, height / 10 + rng.Next(-4, 4));
            Cv2.Ellipse(mat, new Point(fx, fy), fingerAxes, angle + 90, 0, 360, skin, thickness: -1);
        }

        return FrameMat.FromMat(mat);
    }

    /// A filled square (aspect 1:1) -- structurally a clean 4-point convex
    /// quad, so it exists purely to exercise the ASPECT filter.
    public static CameraFrame Square(int width, int height, float sidePx, byte matBrightness = 170, int seed = 4)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        var half = sidePx / 2f;
        var cx = width / 2f;
        var cy = height / 2f;
        var pts = new[]
        {
            new Point(cx - half, cy - half),
            new Point(cx + half, cy - half),
            new Point(cx + half, cy + half),
            new Point(cx - half, cy + half),
        };
        Cv2.FillConvexPoly(mat, pts, new Scalar(20, 20, 20));

        return FrameMat.FromMat(mat);
    }

    /// A flat-filled quad at the CORRECT card aspect ratio, but far too
    /// small to be a real card -- isolates the MINIMUM-AREA filter from the
    /// aspect filter (a tiny card-shaped speck is exactly the case the
    /// aspect filter alone cannot catch).
    public static CameraFrame TinyCardShapedSpeck(int width, int height, float shortSidePx, byte matBrightness = 170, int seed = 7)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        DrawFilledQuad(mat, width / 2f, height / 2f, shortSidePx, shortSidePx * CardAspect, angleDegrees: 0, new Scalar(20, 20, 20));

        return FrameMat.FromMat(mat);
    }

    /// A filled long, thin rectangle (aspect far outside tolerance either
    /// way) -- exercises the ASPECT filter from the other direction.
    public static CameraFrame ThinRectangle(int width, int height, float shortSidePx, float longSidePx, byte matBrightness = 170, int seed = 5)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        var groundTruth = DrawFilledQuad(mat, width / 2f, height / 2f, shortSidePx, longSidePx, 0, new Scalar(20, 20, 20));
        _ = groundTruth;

        return FrameMat.FromMat(mat);
    }

    /// A single upright, flat-filled (no nested bands -- deliberately
    /// simpler than `CardOnMat`, so this test isolates the border filter
    /// from the nested-dedupe concern) card whose left edge sits ONE pixel
    /// inside the frame: fully drawn and fully genuine-gradient-detectable
    /// (there is a real mat pixel to its left for Canny to contrast
    /// against), but within `ContourDetectorOptions.BorderMarginPx`'s
    /// default of 2px -- exercising the border filter without any of a
    /// clipped shape's contour-closure ambiguity.
    public static CameraFrame CardTouchingBorder(int width, int height, float shortSidePx, byte matBrightness = 170, int seed = 6)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.All(matBrightness));
        AddNoise(mat, seed, amplitude: 6);

        var longSidePx = shortSidePx * CardAspect;
        var cx = (shortSidePx / 2f) + 1f; // left edge lands at x=1
        var cy = height / 2f;
        DrawFilledQuad(mat, cx, cy, shortSidePx, longSidePx, angleDegrees: 0, new Scalar(15, 15, 15));

        return FrameMat.FromMat(mat);
    }

    /// Draws a structurally card-like rectangle: a solid outer border
    /// (black), an inset inner frame band (a lighter, distinct gray), and
    /// a further inset "art box" of a DELIBERATELY different aspect ratio
    /// (so it should be rejected by the aspect filter, never by dedupe --
    /// dedupe exists for the inner-frame band, which shares the card's own
    /// aspect). Returns the OUTER quad's ground-truth corners.
    private static CardQuad DrawCard(
        Mat mat, float centerX, float centerY, float shortSidePx, float longSidePx, float angleDegrees, int seedOffset = 0)
    {
        var outer = DrawFilledQuad(mat, centerX, centerY, shortSidePx, longSidePx, angleDegrees, new Scalar(15, 15, 15));

        // Inner frame: a uniform ~12% inset on both axes, same aspect
        // family as the card itself, at a strongly different gray level --
        // real cards look exactly like this (black border, lighter inner
        // frame), and it is what makes nested-quad dedupe necessary: this
        // inner boundary can register as its own contour.
        DrawFilledQuad(mat, centerX, centerY, shortSidePx * 0.78f, longSidePx * 0.82f, angleDegrees, new Scalar(210, 210, 210));

        // Art box: deliberately NOT card-shaped (near-square), so it must
        // be rejected by the ASPECT filter on its own, independent of
        // dedupe.
        DrawFilledQuad(mat, centerX, centerY - (longSidePx * 0.08f), shortSidePx * 0.62f, shortSidePx * 0.62f, angleDegrees, new Scalar(90, 110, 130));

        _ = seedOffset;
        return outer;
    }

    private static CardQuad DrawFilledQuad(
        Mat mat, float centerX, float centerY, float shortSidePx, float longSidePx, float angleDegrees, Scalar color)
    {
        var quad = RotatedQuad(centerX, centerY, shortSidePx, longSidePx, angleDegrees);
        var pts = new[]
        {
            new Point(quad.TL.X, quad.TL.Y),
            new Point(quad.TR.X, quad.TR.Y),
            new Point(quad.BR.X, quad.BR.Y),
            new Point(quad.BL.X, quad.BL.Y),
        };
        Cv2.FillConvexPoly(mat, pts, color);
        return quad;
    }

    /// The 4 corners of a `shortSidePx`-by-`longSidePx` rectangle centered
    /// at (`centerX`,`centerY`), rotated `angleDegrees` about that center.
    /// Local (unrotated) TL is (-short/2, -long/2), matching CardQuad's own
    /// "TL,TR,BR,BL clockwise" convention when `angleDegrees` is 0.
    public static CardQuad RotatedQuad(float centerX, float centerY, float shortSidePx, float longSidePx, float angleDegrees)
    {
        var theta = angleDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(theta);
        var sin = MathF.Sin(theta);

        PointF2 Rotate(float dx, float dy) =>
            new(centerX + (dx * cos) - (dy * sin), centerY + (dx * sin) + (dy * cos));

        var halfW = shortSidePx / 2f;
        var halfH = longSidePx / 2f;

        return new CardQuad(
            TL: Rotate(-halfW, -halfH),
            TR: Rotate(halfW, -halfH),
            BR: Rotate(halfW, halfH),
            BL: Rotate(-halfW, halfH));
    }

    private static void AddNoise(Mat mat, int seed, int amplitude)
    {
        using var noise = new Mat(mat.Size(), mat.Type());
        Cv2.Randu(noise, Scalar.All(-amplitude), Scalar.All(amplitude));
        _ = seed;
        Cv2.Add(mat, noise, mat);
    }
}
