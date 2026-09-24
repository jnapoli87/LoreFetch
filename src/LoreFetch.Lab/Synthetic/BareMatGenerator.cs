using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using OpenCvSharp;

namespace LoreFetch.Lab.Synthetic;

/// The three mat contrasts docs/design/identification.md Risk 3 names:
/// "Detection depends on finding the card's edge, and modern cards are
/// black-bordered, so a dark mat is the worst case." `Dark` is that worst
/// case; `Light`/`Mid` are the other two points the risk asks to be
/// "settled by measurement" across.
public enum MatContrast
{
    Light,
    Mid,
    Dark,
}

public sealed record BareMatOptions
{
    public int FrameWidth { get; init; } = 1920;

    public int FrameHeight { get; init; } = 1080;

    public double NoiseSigma { get; init; } = 6.0;

    /// A faint straight line across the frame, standing in for a mat's own
    /// stitched seam -- a real edge-like feature that is still not a card
    /// and must still be rejected.
    public bool WithSeam { get; init; }

    public int Seed { get; init; } = 1;

    public static BareMatOptions Default { get; } = new();
}

/// Package B7's bare-mat generator: "mat texture with no card, across the
/// light/mid/dark contrasts of Risk 3" -- docs/design/identification.md B7's
/// own justification is that nothing else in the suite can produce this
/// fixture IN CI (a committed real photograph would be card imagery even
/// with no card in frame, if it were ever a real capture of the user's
/// mat, so this stays procedural like `SyntheticFrameGenerator`).
///
/// Produces a `CameraFrame` directly -- there is no card, so there is no
/// question of a hash pipeline to route through; the acceptance criterion
/// this exists for (`ICardDetector.Detect` returning zero quads) only ever
/// calls `Detect`, never `IRectifier`/`ICardIdentifier`.
public static class BareMatGenerator
{
    /// A light mat -- e.g. white card stock or a pale cutting mat.
    public const byte LightBrightness = 210;

    /// A mid-gray mat -- a plausible "default" fabric or foam mat.
    public const byte MidBrightness = 140;

    /// The dark mat DECISIONS.md's Risk 3 calls the worst case: modern cards
    /// are black-bordered, so a dark mat is where the card's own edge has
    /// the least contrast against the background to find.
    public const byte DarkBrightness = 35;

    public static CameraFrame Generate(MatContrast contrast, BareMatOptions? options = null)
    {
        options ??= BareMatOptions.Default;

        var brightness = BrightnessFor(contrast);
        using var mat = new Mat(options.FrameHeight, options.FrameWidth, MatType.CV_8UC3, Scalar.All(brightness));

        SyntheticFrameSupport.AddGaussianNoise(mat, options.NoiseSigma, options.Seed);

        if (options.WithSeam)
        {
            var y = options.FrameHeight / 2;
            var seamShade = (byte)Math.Clamp(brightness - 20, 0, 255);
            Cv2.Line(mat, new Point(0, y), new Point(options.FrameWidth - 1, y), new Scalar(seamShade, seamShade, seamShade), 2);
        }

        using var compressed = SyntheticFrameSupport.JpegRoundTrip(mat, quality: 90);
        return FrameMat.FromMat(compressed);
    }

    private static byte BrightnessFor(MatContrast contrast) => contrast switch
    {
        MatContrast.Light => LightBrightness,
        MatContrast.Mid => MidBrightness,
        MatContrast.Dark => DarkBrightness,
        _ => throw new ArgumentOutOfRangeException(nameof(contrast), contrast, "Unknown MatContrast."),
    };
}
