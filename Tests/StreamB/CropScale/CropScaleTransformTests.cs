using LoreFetch.Lab.CropScale;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB.CropScale;

/// Package B5c: `CropScaleTransform` is diagnostic tooling for the crop-
/// scale experiment, not part of the shipping reference/query path, so
/// these tests pin its GEOMETRIC behaviour (which direction is "inset" vs
/// "outset", that the two axes are independent, that zero fraction is a
/// no-op) rather than bit-exactness against any golden hash.
public class CropScaleTransformTests
{
    private const int Width = 200;
    private const int Height = 280;

    [Fact]
    public void ZeroFraction_IsApproximatelyIdentity()
    {
        using var source = SyntheticImages.MakeCardLikeBgr(Width, Height, seed: 7001);
        using var result = CropScaleTransform.Apply(source, 0f, 0f);

        Assert.Equal(source.Rows, result.Rows);
        Assert.Equal(source.Cols, result.Cols);

        using var diff = new Mat();
        Cv2.Absdiff(source, result, diff);
        var meanDiff = Cv2.Mean(diff).Val0;

        // GetRectSubPix's bilinear resampling at a fractional centre is not
        // guaranteed bit-exact even when patchSize matches the source, but
        // a zero-fraction call must be visually a no-op: comfortably under
        // one intensity level on average.
        Assert.True(meanDiff < 1.0, $"Zero-fraction call should be near-identity; mean abs diff was {meanDiff:F3}.");
    }

    /// A marker image: a bright square sits in the exact centre of a dark
    /// field. Cropping the source SMALLER (positive inset) then stretching
    /// it back up must make the bright square occupy a LARGER share of the
    /// output -- that is the whole mechanism ("zoom in") this type exists
    /// to reproduce. Cropping the source LARGER (negative inset / outset)
    /// must make it occupy a SMALLER share. This is the test whose sign
    /// convention is easy to get backwards -- see the chaos note in the
    /// implementation report.
    [Theory]
    [InlineData(0.10f, true)] // inset: bright fraction must GROW
    [InlineData(-0.10f, false)] // outset: bright fraction must SHRINK
    public void CropDirection_ChangesMarkerFractionInTheExpectedDirection(float fraction, bool expectGrowth)
    {
        using var marker = MakeCenteredSquareMarker(Width, Height, squareFraction: 0.4f);
        var originalBrightFraction = BrightFraction(marker);

        using var result = CropScaleTransform.Apply(marker, fraction, fraction);
        var resultBrightFraction = BrightFraction(result);

        if (expectGrowth)
        {
            Assert.True(
                resultBrightFraction > originalBrightFraction + 0.01,
                $"Inset {fraction} should grow the marker's frame share: {originalBrightFraction:F3} -> {resultBrightFraction:F3}.");
        }
        else
        {
            Assert.True(
                resultBrightFraction < originalBrightFraction - 0.01,
                $"Outset {fraction} should shrink the marker's frame share: {originalBrightFraction:F3} -> {resultBrightFraction:F3}.");
        }
    }

    [Fact]
    public void AnisotropicFractions_AffectEachAxisIndependently()
    {
        using var marker = MakeCenteredSquareMarker(Width, Height, squareFraction: 0.4f);

        // Inset width only: the square should widen relative to the frame
        // but its height share should not grow by nearly as much.
        using var widthOnly = CropScaleTransform.Apply(marker, 0.15f, 0f);
        using var heightOnly = CropScaleTransform.Apply(marker, 0f, 0.15f);

        var widthOnlyBounds = BrightBoundingBox(widthOnly);
        var heightOnlyBounds = BrightBoundingBox(heightOnly);

        Assert.True(
            widthOnlyBounds.Width > heightOnlyBounds.Width,
            "A width-only inset should widen the marker more than a height-only inset does.");
        Assert.True(
            heightOnlyBounds.Height > widthOnlyBounds.Height,
            "A height-only inset should heighten the marker more than a width-only inset does.");
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void OutOfRangeFraction_Throws(float widthFraction, float heightFraction)
    {
        using var source = SyntheticImages.MakeCardLikeBgr(Width, Height, seed: 7002);
        Assert.Throws<ArgumentOutOfRangeException>(() => CropScaleTransform.Apply(source, widthFraction, heightFraction));
    }

    [Fact]
    public void NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CropScaleTransform.Apply(null!, 0f, 0f));
    }

    [Fact]
    public void EmptySource_Throws()
    {
        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() => CropScaleTransform.Apply(empty, 0f, 0f));
    }

    private static Mat MakeCenteredSquareMarker(int width, int height, float squareFraction)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(0, 0, 0));
        var squareWidth = (int)(width * squareFraction);
        var squareHeight = (int)(height * squareFraction);
        var x = (width - squareWidth) / 2;
        var y = (height - squareHeight) / 2;
        Cv2.Rectangle(mat, new Rect(x, y, squareWidth, squareHeight), new Scalar(255, 255, 255), thickness: -1);
        return mat;
    }

    private static double BrightFraction(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 127, 255, ThresholdTypes.Binary);
        var brightPixels = Cv2.CountNonZero(mask);
        return (double)brightPixels / (bgr.Rows * bgr.Cols);
    }

    private static Rect BrightBoundingBox(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 127, 255, ThresholdTypes.Binary);
        return Cv2.BoundingRect(mask);
    }
}
