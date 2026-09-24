using OpenCvSharp;

namespace LoreFetch.Lab.CropScale;

/// Package E1a, part of the crop-expansion experiment: measures how much of
/// a canonical (488x680, or any fixed-size) Scryfall render's own real
/// estate is the card's black outer border, by scanning inward from each
/// edge along the mid-row/mid-column until the pixel brightens past the
/// border into the card's inner (coloured) frame. This is a STRUCTURAL
/// measurement of the card art itself -- independent of any detector
/// defect -- and is what predicts how far `QuadExpansion.Expand` needs to
/// grow a quad that landed on the border's INNER edge (the defect this
/// package's orchestrator finding describes on `tight_white` and the
/// matching black-mat frame) to recover the card's TRUE outer edge.
///
/// Diagnostic-only, `LoreFetch.Lab`-scoped: it exists to MEASURE a
/// correction factor from arbitrary samples for `lab expand-experiment` to
/// explore, not to apply one -- `QuadExpansion` itself moved to
/// `Core/Detection` (package DH) once its committed constants
/// (`BorderWidthCorrectionFactor`/`BorderHeightCorrectionFactor`) started
/// shipping in the product's dual-hypothesis identification path; this
/// type stays here because re-measuring the ratio from a fresh sample is
/// still a diagnostic activity, not something the shipped path needs at
/// runtime.
public static class BorderRatioMeasurement
{
    /// A pixel is "still border" while its grayscale value stays at or
    /// below this threshold. Modern MTG card borders render essentially
    /// black (measured well under 30/255 in practice); 50 leaves headroom
    /// for JPEG-compression noise in a Scryfall `normal` render without
    /// mistaking a dark card-frame colour (e.g. a black-bordered card's own
    /// black mana symbol backing) for the border -- callers scan from the
    /// OUTERMOST pixel inward and stop at the FIRST pixel above threshold,
    /// so a false-brighten deep inside the border cannot shorten the
    /// measurement; only a border that never gets this bright at all would
    /// under-measure, and that would show up as an implausibly large
    /// fraction rather than silently passing.
    public const int BorderBrightnessThreshold = 50;

    public readonly record struct BorderMeasurement(
        int LeftPx, int RightPx, int TopPx, int BottomPx, int Width, int Height)
    {
        public float WidthBorderFraction => (LeftPx + RightPx) / (float)Width;

        public float HeightBorderFraction => (TopPx + BottomPx) / (float)Height;

        /// The `QuadExpansion` factor that would grow a quad sitting exactly
        /// on this border's INNER edge back out to the card's true outer
        /// edge, per axis: if the border eats `fraction` of the axis, the
        /// inner span is `(1 - fraction)` of the true span, so the
        /// correction is the reciprocal.
        public float WidthCorrectionFactor => 1f / (1f - WidthBorderFraction);

        public float HeightCorrectionFactor => 1f / (1f - HeightBorderFraction);
    }

    /// Scans inward from each of the four edges along the OPPOSITE mid-line
    /// (mid-row for left/right, mid-column for top/bottom) -- not along a
    /// single diagonal or corner probe, because a corner sits at the
    /// border's thickest (mitred) point and would overstate every edge at
    /// once; the mid-line is the border at its representative, un-mitred
    /// thickness on that one edge.
    public static BorderMeasurement Measure(Mat canonicalBgr)
    {
        ArgumentNullException.ThrowIfNull(canonicalBgr);
        if (canonicalBgr.Empty())
        {
            throw new ArgumentException("BorderRatioMeasurement.Measure: source image is empty.", nameof(canonicalBgr));
        }

        using var gray = new Mat();
        Cv2.CvtColor(canonicalBgr, gray, ColorConversionCodes.BGR2GRAY);

        var width = gray.Cols;
        var height = gray.Rows;
        var midRow = height / 2;
        var midCol = width / 2;

        var left = ScanInward(gray, width, x => gray.At<byte>(midRow, x));
        var right = ScanInward(gray, width, x => gray.At<byte>(midRow, width - 1 - x));
        var top = ScanInward(gray, height, y => gray.At<byte>(y, midCol));
        var bottom = ScanInward(gray, height, y => gray.At<byte>(height - 1 - y, midCol));

        return new BorderMeasurement(left, right, top, bottom, width, height);
    }

    /// Walks `index` from 0 up to (but not including) `limit`, returning the
    /// first `index` whose sampled pixel exceeds `BorderBrightnessThreshold`
    /// -- i.e. how many border pixels were crossed before the inner frame
    /// began. Returns `limit` (the whole span) if no pixel ever brightens,
    /// rather than throwing -- an all-dark scan line is a measurement
    /// result (an implausibly wide "border"), not a caller error.
    private static int ScanInward(Mat gray, int limit, Func<int, byte> sample)
    {
        for (var i = 0; i < limit; i++)
        {
            if (sample(i) > BorderBrightnessThreshold)
            {
                return i;
            }
        }

        return limit;
    }
}
