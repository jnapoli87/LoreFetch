using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using OpenCvSharp;

namespace LoreFetch.Lab.Synthetic;

/// Tunable knobs for `MultiCardFrameGenerator.Generate`. Deliberately a
/// smaller set than `SyntheticFrameOptions` -- this generator exists only
/// to drive package B6's own harness tests (row-major slot mapping, and the
/// count-mismatch-must-not-pair-by-position guard) through the REAL
/// `ContourCardDetector` -> `PerspectiveRectifier` path against a MULTI-card
/// scene, which `SyntheticFrameGenerator` (one card, frame-centered) cannot
/// produce. It does not need `SyntheticFrameGenerator`'s keystone: B6's own
/// tests are about slot ORDER and detection COUNT, not crop-scale or
/// perspective, both of which B5c/B7 already own.
public sealed record MultiCardFrameOptions
{
    public int FrameWidth { get; init; } = 1920;

    public int FrameHeight { get; init; } = 1080;

    public double BlurSigma { get; init; } = 0.6;

    public double NoiseSigma { get; init; } = 5.0;

    public int JpegQuality { get; init; } = 90;

    /// A light mat by default -- CLAUDE.md's own "safest default for
    /// detection" (B5b's real-capture evidence: 4/4 detected and correct on
    /// white/light backgrounds). B6's tests are not re-litigating mat
    /// contrast (Risk 3/4 are B5's own), so there is no reason to default
    /// to a harder case here.
    public byte MatBrightness { get; init; } = 200;

    public int Seed { get; init; } = 1;

    public static MultiCardFrameOptions Default { get; } = new();
}

/// Composites several already-rendered card images into ONE camera-like
/// frame, each at its own caller-supplied fractional center -- unlike
/// `SyntheticFrameGenerator`, which always places exactly one card at the
/// frame's own center. Every card is placed axis-aligned (no keystone): the
/// tests this generator drives are about which SLOT a detected quad maps to
/// and about what happens when the detected COUNT disagrees with the
/// ground truth, neither of which needs perspective distortion to exercise
/// -- B7 already owns the keystone/crop-scale risk.
public static class MultiCardFrameGenerator
{
    public const float CardWidthInches = 2.5f;

    public const float CardHeightInches = 3.5f;

    public const float PxPerInchNumerator = 1360f;

    /// `centersFrac` are fractions (0..1) of the frame's own width/height,
    /// one per `sourceCardsBgr` entry, in the SAME order -- this generator
    /// makes no claim about which slot a center corresponds to; that is
    /// exactly what the caller's test is verifying downstream via
    /// `SlotMapper`/`AccuracyFrameRunner`, so this type must not itself
    /// impose or assume row-major order on its inputs.
    public static CameraFrame Generate(
        IReadOnlyList<Mat> sourceCardsBgr, IReadOnlyList<(float XFrac, float YFrac)> centersFrac,
        float heightInches, MultiCardFrameOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCardsBgr);
        ArgumentNullException.ThrowIfNull(centersFrac);
        if (sourceCardsBgr.Count != centersFrac.Count)
        {
            throw new ArgumentException(
                $"MultiCardFrameGenerator.Generate: {sourceCardsBgr.Count} card(s) but {centersFrac.Count} center(s).",
                nameof(centersFrac));
        }

        if (heightInches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightInches), heightInches, "heightInches must be positive.");
        }

        options ??= MultiCardFrameOptions.Default;

        var pxPerInch = PxPerInchNumerator / heightInches;
        var cardWidthPx = Math.Max(2, (int)MathF.Round(CardWidthInches * pxPerInch));
        var cardHeightPx = Math.Max(2, (int)MathF.Round(CardHeightInches * pxPerInch));

        using var frame = new Mat(options.FrameHeight, options.FrameWidth, MatType.CV_8UC3, Scalar.All(options.MatBrightness));

        for (var i = 0; i < sourceCardsBgr.Count; i++)
        {
            using var downscaled = new Mat();
            Cv2.Resize(sourceCardsBgr[i], downscaled, new Size(cardWidthPx, cardHeightPx), 0, 0, InterpolationFlags.Area);

            var (xFrac, yFrac) = centersFrac[i];
            var centerX = (int)MathF.Round(xFrac * options.FrameWidth);
            var centerY = (int)MathF.Round(yFrac * options.FrameHeight);
            var left = centerX - (cardWidthPx / 2);
            var top = centerY - (cardHeightPx / 2);

            if (left < 0 || top < 0 || left + cardWidthPx > options.FrameWidth || top + cardHeightPx > options.FrameHeight)
            {
                throw new ArgumentException(
                    $"MultiCardFrameGenerator.Generate: card {i} at center ({xFrac:F2},{yFrac:F2}) does not fit " +
                    $"inside the {options.FrameWidth}x{options.FrameHeight} frame at {cardWidthPx}x{cardHeightPx}px.",
                    nameof(centersFrac));
            }

            using var region = new Mat(frame, new Rect(left, top, cardWidthPx, cardHeightPx));
            downscaled.CopyTo(region);
        }

        Cv2.GaussianBlur(frame, frame, new Size(0, 0), options.BlurSigma);
        SyntheticFrameSupport.AddGaussianNoise(frame, options.NoiseSigma, options.Seed);

        using var compressed = SyntheticFrameSupport.JpegRoundTrip(frame, options.JpegQuality);
        return FrameMat.FromMat(compressed);
    }
}
