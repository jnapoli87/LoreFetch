using LoreFetch.Core.Abstractions;
using LoreFetch.Lab.Synthetic;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Lab;

/// Unit-level coverage for `SyntheticFrameGenerator` itself, separate from
/// `SyntheticFrameGeneratorRoundTripTests`'s end-to-end identification
/// check: these tests never touch `ICardDetector`/`IRectifier`/
/// `ICardIdentifier` at all, so a mutation caught here is caught
/// independently of whatever `ContourCardDetector` happens to do with the
/// result.
public class SyntheticFrameGeneratorTests
{
    /// Chaos case (d) (brief-mandated): pins the generated card's pixel
    /// size against a value computed BY THIS TEST, independently of
    /// `SyntheticFrameGenerator.PxPerInchNumerator`/`CardWidthInches`/
    /// `CardHeightInches` -- reusing those constants in the assertion
    /// would make the test agree with the production formula by
    /// construction, no matter what that formula says. 9.75in x
    /// (1360/9.75) px/inch, from CLAUDE.md's own "Geometry" table, gives
    /// 348.7 x 488.2px, i.e. 349 x 488 once rounded -- computed here from
    /// the raw numbers, not from the type under test.
    [Fact]
    public void Generate_AtNineSeventyFiveInches_ProducesTheDocumentedCardPixelSize()
    {
        using var source = MakeSourceCard(seed: 1);
        var result = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f);
        using (result.Frame)
        {
            Assert.Equal(349, (int)result.ExpectedCardWidthPx);
            Assert.Equal(488, (int)result.ExpectedCardHeightPx);
        }
    }

    /// A second, independently-computed height: at 3.1in (CLAUDE.md's
    /// single-card minimum height) px/inch = 1360/3.1 = 438.7, giving a
    /// card of 1096.8 x 1535.3px -- rounds to 1097 x 1535. Two points on
    /// the curve, not one, make it harder for a mutated formula to
    /// coincidentally still pass at the ONE height the test above checks.
    [Fact]
    public void Generate_AtDifferentHeight_ScalesInverselyWithHeight()
    {
        using var source = MakeSourceCard(seed: 2);

        // A frame big enough to hold the much larger card this shorter
        // height implies -- the 1920x1080 default does not fit it.
        var options = new SyntheticFrameOptions { FrameWidth = 2400, FrameHeight = 2400 };
        var result = SyntheticFrameGenerator.Generate(source, heightInches: 3.1f, options);
        using (result.Frame)
        {
            Assert.Equal(1097, (int)result.ExpectedCardWidthPx);
            Assert.Equal(1535, (int)result.ExpectedCardHeightPx);
        }
    }

    /// Reviewer finding (2026-09-22): `SyntheticFrameOptions.Default` must
    /// use `INTER_AREA` for the downscale -- CLAUDE.md's own "by choice,
    /// not by fidelity" reasoning for `ReferenceTransform`'s equivalent
    /// step applies identically here (see `DownscaleInterpolation`'s own
    /// doc comment). Paired with `Generate_AreaVsLinearDownscale_
    /// ProducesDifferentPixels_AndDefaultMatchesArea` below: together they
    /// pin the DEFAULT by observable behaviour, not just by reading the
    /// property initializer, so an edit to the call site that stops
    /// reading `options.DownscaleInterpolation` (chaos case (b)) is caught
    /// even though this declaration-level check alone would not catch it.
    [Fact]
    public void Default_UsesAreaForDownscale()
    {
        Assert.Equal(InterpolationFlags.Area, SyntheticFrameOptions.Default.DownscaleInterpolation);
    }

    /// Chaos case (b) (brief-mandated, closed per reviewer direction
    /// 2026-09-22): closes the gap the round-trip test's own chaos
    /// experiment found -- swapping `Generate`'s hardcoded downscale
    /// filter from `INTER_AREA` to `INTER_LINEAR` left all 179 tests
    /// green, because nothing asserted on the FILTER CHOICE itself, only
    /// on end-to-end identification (which the mild 9.75in downscale
    /// apparently survives either way). This closes it differentially --
    /// architecture-independent, unlike a golden hash, because both sides
    /// of the comparison are computed on whatever machine runs the test:
    ///   1. `Area` and `Linear`, same source/seed/height, must produce
    ///      DIFFERENT pixels (proves the option is real, not a no-op).
    ///   2. The DEFAULT output must be BYTE-IDENTICAL to the explicit
    ///      `Area` output (pins which of the two the default actually is).
    /// Together, an unintended edit to the `Cv2.Resize` call at line ~162
    /// -- whether it stops reading `options.DownscaleInterpolation`, or
    /// starts ignoring it via a hardcoded literal -- fails (2), which is
    /// the actual risk this reviewer finding is closing.
    [Fact]
    public void Generate_AreaVsLinearDownscale_ProducesDifferentPixels_AndDefaultMatchesArea()
    {
        using var source = MakeSourceCard(seed: 7);

        var areaOptions = new SyntheticFrameOptions { DownscaleInterpolation = InterpolationFlags.Area };
        var linearOptions = new SyntheticFrameOptions { DownscaleInterpolation = InterpolationFlags.Linear };

        var areaResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, areaOptions);
        var linearResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, linearOptions);
        var defaultResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f);

        using (areaResult.Frame)
        using (linearResult.Frame)
        using (defaultResult.Frame)
        {
            using var areaMat = Core.Detection.FrameMat.ToMat(areaResult.Frame);
            using var linearMat = Core.Detection.FrameMat.ToMat(linearResult.Frame);
            using var defaultMat = Core.Detection.FrameMat.ToMat(defaultResult.Frame);

            using var areaVsLinearDiff = new Mat();
            Cv2.Absdiff(areaMat, linearMat, areaVsLinearDiff);
            var areaVsLinearDiffering = Cv2.CountNonZero(ToSingleChannelAny(areaVsLinearDiff));

            Assert.True(
                areaVsLinearDiffering > 0,
                "Area and Linear downscale produced byte-identical frames -- DownscaleInterpolation is not " +
                "actually reaching Generate's Cv2.Resize call.");

            using var defaultVsAreaDiff = new Mat();
            Cv2.Absdiff(defaultMat, areaMat, defaultVsAreaDiff);
            var defaultVsAreaDiffering = Cv2.CountNonZero(ToSingleChannelAny(defaultVsAreaDiff));

            Assert.True(
                defaultVsAreaDiffering == 0,
                "Generate's default output does not byte-match its own explicit Area output -- the default " +
                "downscale filter has drifted away from Area.");
        }
    }

    /// Reviewer finding (2026-09-22, second instance): the keystone warp's
    /// own interpolation flag was ALSO a hardcoded literal, one step
    /// further down `Generate`'s pipeline than the downscale -- found by
    /// the reviewer chaos-testing an un-briefed spot (`Linear` ->
    /// `Nearest` at the `Cv2.WarpPerspective` call left all 182 tests
    /// green). Same closure as `Default_UsesAreaForDownscale`: declares
    /// what the default must be.
    [Fact]
    public void Default_UsesLinearForKeystone()
    {
        Assert.Equal(InterpolationFlags.Linear, SyntheticFrameOptions.Default.KeystoneInterpolation);
    }

    /// The behavioural half of closing the same finding: `Linear` and
    /// `Nearest`, same source/seed/height/keystone amount, must produce
    /// DIFFERENT pixels (proves `KeystoneInterpolation` is real, not a
    /// no-op), and the DEFAULT output must byte-match the explicit
    /// `Linear` output (pins which of the two the default actually is).
    /// Mirrors `Generate_AreaVsLinearDownscale_ProducesDifferentPixels_
    /// AndDefaultMatchesArea` exactly, one step later in the pipeline: an
    /// unintended edit to the `Cv2.WarpPerspective` call in `ApplyKeystone`
    /// -- whether it stops reading `keystoneInterpolation`, or starts
    /// ignoring it via a hardcoded literal -- fails the default-matches-
    /// Linear half of this test.
    [Fact]
    public void Generate_KeystoneLinearVsNearest_ProducesDifferentPixels_AndDefaultMatchesLinear()
    {
        using var source = MakeSourceCard(seed: 8);

        var linearOptions = new SyntheticFrameOptions { KeystoneInterpolation = InterpolationFlags.Linear };
        var nearestOptions = new SyntheticFrameOptions { KeystoneInterpolation = InterpolationFlags.Nearest };

        var linearResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, linearOptions);
        var nearestResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, nearestOptions);
        var defaultResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f);

        using (linearResult.Frame)
        using (nearestResult.Frame)
        using (defaultResult.Frame)
        {
            using var linearMat = Core.Detection.FrameMat.ToMat(linearResult.Frame);
            using var nearestMat = Core.Detection.FrameMat.ToMat(nearestResult.Frame);
            using var defaultMat = Core.Detection.FrameMat.ToMat(defaultResult.Frame);

            using var linearVsNearestDiff = new Mat();
            Cv2.Absdiff(linearMat, nearestMat, linearVsNearestDiff);
            var linearVsNearestDiffering = Cv2.CountNonZero(ToSingleChannelAny(linearVsNearestDiff));

            Assert.True(
                linearVsNearestDiffering > 0,
                "Linear and Nearest keystone interpolation produced byte-identical frames -- " +
                "KeystoneInterpolation is not actually reaching ApplyKeystone's Cv2.WarpPerspective call.");

            using var defaultVsLinearDiff = new Mat();
            Cv2.Absdiff(defaultMat, linearMat, defaultVsLinearDiff);
            var defaultVsLinearDiffering = Cv2.CountNonZero(ToSingleChannelAny(defaultVsLinearDiff));

            Assert.True(
                defaultVsLinearDiffering == 0,
                "Generate's default output does not byte-match its own explicit Linear keystone output -- " +
                "the default keystone interpolation has drifted away from Linear.");
        }
    }

    /// Chaos case (e) (my own): what if `Generate` stopped calling the JPEG
    /// round trip -- e.g. someone "simplifies" it away, or the quality
    /// option silently stops being wired through? Two frames built from
    /// the SAME source, seed, keystone and blur/noise settings, differing
    /// ONLY in `JpegQuality`, must produce measurably different pixels: a
    /// generator that dropped the JPEG step (or hardcoded the quality)
    /// would make this assertion fail, because both calls would then be
    /// byte-identical. This is exactly the class of gap this stream has
    /// already been bitten by twice (B1a's step-5 filter swap leaving all
    /// 29 invariant tests green) -- a documented parameter that nothing
    /// actually pins.
    [Fact]
    public void Generate_DifferentJpegQuality_ProducesDetectablyDifferentPixels()
    {
        using var source = MakeSourceCard(seed: 3);

        var highQuality = new SyntheticFrameOptions { JpegQuality = 95 };
        var lowQuality = new SyntheticFrameOptions { JpegQuality = 15 };

        var highResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, highQuality);
        var lowResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, lowQuality);

        using (highResult.Frame)
        using (lowResult.Frame)
        {
            using var highMat = Core.Detection.FrameMat.ToMat(highResult.Frame);
            using var lowMat = Core.Detection.FrameMat.ToMat(lowResult.Frame);

            using var diff = new Mat();
            Cv2.Absdiff(highMat, lowMat, diff);
            var differingPixels = Cv2.CountNonZero(ToSingleChannelAny(diff));

            Assert.True(
                differingPixels > 0,
                "Frames generated at very different JPEG qualities (95 vs 15), everything else held fixed, " +
                "were byte-identical -- JpegQuality is not actually reaching the encoder.");
        }
    }

    /// Same shape of check for the other three named parameters together --
    /// not because they need to be pinned individually (the round-trip
    /// test already exercises all of them via `SyntheticFrameOptions.Default`),
    /// but because "explicit and testable" (this package's own brief) means
    /// each one is verifiably wired, not merely declared.
    [Fact]
    public void Generate_MildVsStrongDegradation_ProducesDetectablyDifferentPixels()
    {
        using var source = MakeSourceCard(seed: 4);

        var mild = new SyntheticFrameOptions { KeystoneAmount = 0f, BlurSigma = 0.01, NoiseSigma = 0, JpegQuality = 100 };
        var strong = new SyntheticFrameOptions { KeystoneAmount = 0.12f, BlurSigma = 2.5, NoiseSigma = 20, JpegQuality = 20 };

        var mildResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, mild);
        var strongResult = SyntheticFrameGenerator.Generate(source, heightInches: 9.75f, strong);

        using (mildResult.Frame)
        using (strongResult.Frame)
        {
            using var mildMat = Core.Detection.FrameMat.ToMat(mildResult.Frame);
            using var strongMat = Core.Detection.FrameMat.ToMat(strongResult.Frame);

            using var diff = new Mat();
            Cv2.Absdiff(mildMat, strongMat, diff);
            var meanDiff = Cv2.Mean(ToSingleChannelAny(diff)).Val0;

            Assert.True(
                meanDiff > 3.0,
                $"Mild vs strong degradation options produced near-identical frames (mean abs diff {meanDiff:F2}) -- " +
                "KeystoneAmount/BlurSigma/NoiseSigma/JpegQuality are not detectably affecting the output.");
        }
    }

    [Fact]
    public void Generate_CardTooLargeForFrame_ThrowsArgumentException()
    {
        using var source = MakeSourceCard(seed: 5);

        // At 0.5in, px/inch = 2720, so a 2.5x3.5in card is 6800x9520px --
        // far larger than the default 1920x1080 frame.
        Assert.Throws<ArgumentException>(() => SyntheticFrameGenerator.Generate(source, heightInches: 0.5f));
    }

    [Fact]
    public void Generate_NullSource_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SyntheticFrameGenerator.Generate(null!, heightInches: 9.75f));
    }

    [Fact]
    public void Generate_NonPositiveHeight_ThrowsArgumentOutOfRangeException()
    {
        using var source = MakeSourceCard(seed: 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticFrameGenerator.Generate(source, heightInches: 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticFrameGenerator.Generate(source, heightInches: -1f));
    }

    private static Mat MakeSourceCard(int seed) =>
        SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed);

    private static Mat ToSingleChannelAny(Mat diffBgr)
    {
        if (diffBgr.Channels() == 1)
        {
            return diffBgr;
        }

        var channels = Cv2.Split(diffBgr);
        try
        {
            var combined = new Mat();
            Cv2.Max(channels[0], channels[1], combined);
            Cv2.Max(combined, channels[2], combined);
            return combined;
        }
        finally
        {
            foreach (var c in channels)
            {
                c.Dispose();
            }
        }
    }
}
