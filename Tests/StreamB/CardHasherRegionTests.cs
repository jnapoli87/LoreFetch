using LoreFetch.Core.Imaging;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Step 4 takes only the top `width * 0.85` of the card -- title, art and
/// type line -- and nothing below it may influence the hash. This is the
/// region a full-art land breaks (Risk 5 / stream-b-identification.md
/// "reviewer should scrutinise"), so it earns its own coverage rather than
/// riding along inside a bigger test.
public class CardHasherRegionTests
{
    private const int Width = 200;
    private const int Height = 300; // > width * 0.85 (170), so there IS a below-region area to vary

    [Fact]
    public void ToIcon_ContentBelowZeroPointEightFiveWidth_DoesNotAffectTheHash()
    {
        var regionHeight = (int)MathF.Round(Width * 0.85f); // 170

        using var top = SyntheticImages.MakeCardLikeBgr(Width, Height, seed: 7);
        using var topGray = new Mat();
        Cv2.CvtColor(top, topGray, ColorConversionCodes.BGR2GRAY);

        using var variantA = topGray.Clone();
        using var variantB = topGray.Clone();

        // Identical above the region boundary; wildly different below it
        // (solid black vs. solid white) so even a one-row leak would flip
        // bits after the resize.
        using (var belowA = new Mat(variantA, new Rect(0, regionHeight, Width, Height - regionHeight)))
        {
            belowA.SetTo(new Scalar(0));
        }

        using (var belowB = new Mat(variantB, new Rect(0, regionHeight, Width, Height - regionHeight)))
        {
            belowB.SetTo(new Scalar(255));
        }

        var hashA = CardHasher.Hash(variantA);
        var hashB = CardHasher.Hash(variantB);

        Assert.Equal(0, hashA.HammingDistance(hashB));
        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void ToIcon_RegionHeight_IsEightyFivePercentOfWidth_RoundedAndClamped()
    {
        // A source shorter than 0.85 * its own width (not a real card, but a
        // defensive case) must clamp to the image's actual height rather
        // than asking Cv2 for rows that don't exist.
        using var shortImage = new Mat(50, Width, MatType.CV_8UC1, Scalar.All(80));

        using var icon = CardHasher.ToIcon(shortImage);

        Assert.Equal(CardHasher.IconSize, icon.Rows);
        Assert.Equal(CardHasher.IconSize, icon.Cols);
    }
}
