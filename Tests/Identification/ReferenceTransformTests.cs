using LoreFetch.Core.Identification;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// B1's invariants, restated as bounds rather than equalities -- per
/// DECISIONS.md and docs/design/identification.md, none of these are exact
/// invariants of a lossy, order-statistic-based hash. Each bound below was
/// set from a value measured against these exact generators (see the
/// comment on each test) and then rounded up with headroom, not picked
/// blind.
public class ReferenceTransformTests
{
    private readonly ITestOutputHelper _output;

    public ReferenceTransformTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SameImage_AtTwoInputScales_StaysWithinASmallDistanceBound()
    {
        // Simulates building the reference side from two different source
        // resolutions of the same artwork (e.g. Scryfall `normal` vs a
        // hypothetical `large`) -- INTER_AREA from different source sizes is
        // a different box filter each time, so bits near a cell's median
        // can flip. Not an invariant; measured on win-x64 was 4/1024. The
        // bound leaves headroom for running on a different architecture
        // (INTER_AREA is not bit-exact across x86-64/ARM64 -- DECISIONS.md
        // "The one gate that matters most") without chasing a platform-tuned
        // number here.
        using var full = SyntheticImages.MakeCardLikeBgr(976, 1360, seed: 1);
        using var half = new Mat();
        Cv2.Resize(full, half, new Size(488, 680), 0, 0, InterpolationFlags.Area);

        using var grayFull = ReferenceTransform.Prepare(full);
        using var grayHalf = ReferenceTransform.Prepare(half);

        var hashFull = CardHasher.Hash(grayFull);
        var hashHalf = CardHasher.Hash(grayHalf);
        var distance = hashFull.HammingDistance(hashHalf);

        _output.WriteLine($"two-scale distance = {distance}");
        Assert.InRange(distance, 0, 100);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public void GlobalBrightnessOffset_StaysWithinASmallDistanceBound(double delta)
    {
        // A local-median bit is invariant to a brightness shift ONLY where
        // the shift doesn't saturate at 0/255 -- a card-like gradient has
        // some pixels near either rail, so this is a bound, not zero.
        // Measured on win-x64: 14 at +30, 7 at -30.
        using var baseImage = SyntheticImages.MakeCardLikeBgr(488, 680, seed: 2);
        using var shifted = SyntheticImages.AddBrightness(baseImage, delta);

        using var grayBase = ReferenceTransform.Prepare(baseImage);
        using var grayShifted = ReferenceTransform.Prepare(shifted);

        var distance = CardHasher.Hash(grayBase).HammingDistance(CardHasher.Hash(grayShifted));

        _output.WriteLine($"brightness({delta}) distance = {distance}");
        Assert.InRange(distance, 0, 60);
    }

    [Theory]
    [InlineData(1.3)]
    [InlineData(0.7)]
    public void GammaShift_StaysWithinASmallDistanceBound(double gamma)
    {
        // Gamma preserves ordering (v > m survives), but the tie-break tests
        // the POST-gamma value against the fixed constant 128, so cells
        // whose median sits near 128 can flip. Measured on win-x64: 17 at
        // gamma 1.3, 19 at gamma 0.7 -- small, but not zero, unlike a pure
        // brightness offset.
        using var baseImage = SyntheticImages.MakeCardLikeBgr(488, 680, seed: 3);
        using var gammaShifted = SyntheticImages.ApplyGamma(baseImage, gamma);

        using var grayBase = ReferenceTransform.Prepare(baseImage);
        using var grayGamma = ReferenceTransform.Prepare(gammaShifted);

        var distance = CardHasher.Hash(grayBase).HammingDistance(CardHasher.Hash(grayGamma));

        _output.WriteLine($"gamma({gamma}) distance = {distance}");
        Assert.InRange(distance, 0, 120);
    }

    [Fact]
    public void InvertedImage_LandsNearMaximumDistance()
    {
        // Inversion flips v > m to v < m for every non-tied pixel, and flips
        // the tie-break's m > 128 test too, so distance is close to but not
        // exactly 1024 -- ties whose median sits at exactly 127/128 can
        // land either side after inversion. Measured on win-x64: 1002/1024.
        using var baseImage = SyntheticImages.MakeCardLikeBgr(488, 680, seed: 4);
        using var inverted = SyntheticImages.Invert(baseImage);

        using var grayBase = ReferenceTransform.Prepare(baseImage);
        using var grayInverted = ReferenceTransform.Prepare(inverted);

        var distance = CardHasher.Hash(grayBase).HammingDistance(CardHasher.Hash(grayInverted));

        _output.WriteLine($"inversion distance = {distance}");
        Assert.InRange(distance, 900, 1024);
    }

    [Fact]
    public void Prepare_NullOrEmptySource_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReferenceTransform.Prepare(null!));

        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() => ReferenceTransform.Prepare(empty));
    }
}
