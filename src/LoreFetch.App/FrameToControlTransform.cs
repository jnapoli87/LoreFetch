using Avalonia;
using LoreFetch.Core.Abstractions;

namespace LoreFetch.App;

/// Pure frame-coordinates -> control-coordinates mapping for the quad
/// overlay (A3). `PreviewImage` renders with `Stretch="Uniform"`, so the
/// frame is letterboxed inside the control rather than filling it: the
/// image is scaled by the SMALLER of the two axis ratios (so it never
/// overflows either dimension) and then centred on whichever axis has slack
/// left over. Getting either half wrong — `Max` instead of `Min`, or
/// dropping the centring offset — silently misplaces every quad without
/// ever throwing, which is why this is split out as its own pure, directly
/// testable function rather than buried in the render callback.
internal readonly record struct FrameToControlTransform(double Scale, double OffsetX, double OffsetY)
{
    public static FrameToControlTransform Compute(double frameWidth, double frameHeight, double controlWidth, double controlHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Frame dimensions must be positive.");
        }

        if (controlWidth <= 0 || controlHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(controlWidth), "Control dimensions must be positive.");
        }

        // Stretch="Uniform": scale by the SMALLER ratio so the whole frame
        // fits inside the control on both axes, then centre on the axis
        // that has slack left over (the letterbox bars).
        var scale = Math.Min(controlWidth / frameWidth, controlHeight / frameHeight);
        var offsetX = (controlWidth - (frameWidth * scale)) / 2.0;
        var offsetY = (controlHeight - (frameHeight * scale)) / 2.0;

        return new FrameToControlTransform(scale, offsetX, offsetY);
    }

    /// Maps one frame-coordinate corner (a `CardQuad` corner, always in
    /// frame pixels) to the equivalent point in control coordinates.
    public Point Apply(PointF2 framePoint) =>
        new(OffsetX + (framePoint.X * Scale), OffsetY + (framePoint.Y * Scale));
}
