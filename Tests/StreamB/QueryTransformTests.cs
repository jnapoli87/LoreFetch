using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Pins `QueryTransform` to exactly "grayscale, nothing else" -- no blur, no
/// resize. The reference side's blur+96px resize existing on the query side
/// too would delete the asymmetry the whole algorithm depends on (see
/// `ReferenceTransform`'s doc comment), so this is the regression test for
/// that specific mistake, not a general smoke test.
public class QueryTransformTests
{
    [Fact]
    public void Prepare_IsGrayscaleOnly_MatchesADirectCvtColor_WithNoBlurOrResize()
    {
        // The highest-frequency input available: a 3x3 blur collapses this
        // pattern almost to flat grey, so if Prepare ever gained the
        // reference side's blur, this comparison would catch it loudly.
        using var checkerboard = SyntheticImages.MakeHighFrequencyCheckerboardBgr(
            RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight);
        var card = SyntheticImages.ToRectifiedCard(checkerboard);

        using var raw = QueryTransform.ToMat(card);
        using var expected = new Mat();
        Cv2.CvtColor(raw, expected, ColorConversionCodes.BGR2GRAY);

        using var actual = QueryTransform.Prepare(card);

        Assert.Equal(RectifiedCard.CanonicalWidth, actual.Cols);
        Assert.Equal(RectifiedCard.CanonicalHeight, actual.Rows);
        Assert.True(SyntheticImages.GraysAreIdentical(expected, actual), "QueryTransform.Prepare must be pixel-identical to a direct BGR2GRAY with no blur or resize.");
    }

    [Fact]
    public void ToMat_RoundTripsPixelsExactly_ForBgr24()
    {
        using var source = SyntheticImages.MakeCardLikeBgr(
            RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 5);
        var card = SyntheticImages.ToRectifiedCard(source);

        using var roundTripped = QueryTransform.ToMat(card);

        using var diff = new Mat();
        Cv2.Absdiff(source, roundTripped, diff);
        using var grayDiff = new Mat();
        Cv2.CvtColor(diff, grayDiff, ColorConversionCodes.BGR2GRAY);

        Assert.Equal(0, Cv2.CountNonZero(grayDiff));
    }

    [Fact]
    public void Prepare_NullCard_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QueryTransform.Prepare(null!));
    }
}
