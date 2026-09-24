using LoreFetch.Core.Imaging;
using LoreFetch.Lab.Synthetic;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Package B7's own "Done when" criterion: "detection returns 0 on a bare
/// mat" at each of the light/mid/dark contrasts docs/design/identification.md
/// Risk 3 names -- run against `BareMatGenerator`'s PRODUCTION output (not
/// `DetectorTestFrames.EmptyMat`, which is `Tests/StreamB`-only fixture code
/// that `LoreFetch.Lab` cannot reference; this is the test that exercises
/// B7's own generator, which is what the `lab synth mat` command also
/// writes to disk for a by-eye check).
public class BareMatGeneratorTests
{
    [Theory]
    [InlineData(MatContrast.Light)]
    [InlineData(MatContrast.Mid)]
    [InlineData(MatContrast.Dark)] // Risk 3's worst case -- black-bordered cards against a dark mat
    public void Detect_BareMat_ReturnsZero(MatContrast contrast)
    {
        using var frame = BareMatGenerator.Generate(contrast);
        var detector = new ContourCardDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContourCardDetector>.Instance);

        var detected = detector.Detect(frame, maxCards: 9);

        Assert.Empty(detected);
    }

    [Theory]
    [InlineData(MatContrast.Light)]
    [InlineData(MatContrast.Mid)]
    [InlineData(MatContrast.Dark)]
    public void Detect_BareMatWithSeam_ReturnsZero(MatContrast contrast)
    {
        using var frame = BareMatGenerator.Generate(contrast, new BareMatOptions { WithSeam = true });
        var detector = new ContourCardDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContourCardDetector>.Instance);

        var detected = detector.Detect(frame, maxCards: 9);

        Assert.Empty(detected);
    }

    /// Pins the three contrasts' actual ordering (light brighter than mid,
    /// mid brighter than dark) against literal expected values -- a guard
    /// against `LightBrightness`/`MidBrightness`/`DarkBrightness` ever
    /// being accidentally transposed, which the `Detect_BareMat_ReturnsZero`
    /// theory above could not catch on its own: zero detections on a bare
    /// mat holds at ANY brightness, so a swap between "light" and "dark"
    /// would leave that test green while silently mislabeling which
    /// contrast is actually being exercised as Risk 3's worst case.
    [Fact]
    public void BrightnessConstants_AreOrderedLightMidDark()
    {
        Assert.True(BareMatGenerator.LightBrightness > BareMatGenerator.MidBrightness);
        Assert.True(BareMatGenerator.MidBrightness > BareMatGenerator.DarkBrightness);
    }
}
