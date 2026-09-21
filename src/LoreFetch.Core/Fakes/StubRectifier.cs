using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Managed-code-only crop + nearest-neighbour resize of the quad's bounding
/// box to the canonical 488x680. Deliberately wrong for hashing — the point
/// is that it is obviously wrong for that and obviously fine for thumbnails
/// and wiring, with no OpenCvSharp dependency in the fake.
public sealed class StubRectifier : IRectifier
{
    public RectifiedCard Rectify(CameraFrame frame, CardQuad quad)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bytesPerPixel = BytesPerPixel(frame.Layout);
        var (left, top, right, bottom) = ClampedBoundingBox(quad, frame.Width, frame.Height);
        var srcWidth = right - left;
        var srcHeight = bottom - top;

        const int destWidth = RectifiedCard.CanonicalWidth;
        const int destHeight = RectifiedCard.CanonicalHeight;
        var destStride = destWidth * bytesPerPixel;

        var destPixels = new byte[destStride * destHeight];
        var srcPixels = frame.Pixels.Span;
        var srcStride = frame.Stride;

        for (var y = 0; y < destHeight; y++)
        {
            var srcY = top + Math.Min(srcHeight - 1, (int)((y / (float)destHeight) * srcHeight));
            var srcRowOffset = srcY * srcStride;
            var destRowOffset = y * destStride;

            for (var x = 0; x < destWidth; x++)
            {
                var srcX = left + Math.Min(srcWidth - 1, (int)((x / (float)destWidth) * srcWidth));
                var srcOffset = srcRowOffset + (srcX * bytesPerPixel);
                var destOffset = destRowOffset + (x * bytesPerPixel);

                srcPixels.Slice(srcOffset, bytesPerPixel).CopyTo(destPixels.AsSpan(destOffset, bytesPerPixel));
            }
        }

        return new RectifiedCard(destPixels, destStride, frame.Layout, quad);
    }

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgr24 => 3,
        PixelLayout.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unsupported pixel layout."),
    };

    /// The quad's axis-aligned bounding box, clamped to the frame so a quad
    /// that runs partly (or entirely) off-frame — a bad manual quad, or a
    /// detector edge case — can never make the crop read out of bounds.
    private static (int Left, int Top, int Right, int Bottom) ClampedBoundingBox(
        CardQuad quad, int frameWidth, int frameHeight)
    {
        var minX = Min4(quad.TL.X, quad.TR.X, quad.BR.X, quad.BL.X);
        var maxX = Max4(quad.TL.X, quad.TR.X, quad.BR.X, quad.BL.X);
        var minY = Min4(quad.TL.Y, quad.TR.Y, quad.BR.Y, quad.BL.Y);
        var maxY = Max4(quad.TL.Y, quad.TR.Y, quad.BR.Y, quad.BL.Y);

        var left = Math.Clamp((int)MathF.Floor(minX), 0, frameWidth - 1);
        var top = Math.Clamp((int)MathF.Floor(minY), 0, frameHeight - 1);
        var right = Math.Clamp((int)MathF.Ceiling(maxX), left + 1, frameWidth);
        var bottom = Math.Clamp((int)MathF.Ceiling(maxY), top + 1, frameHeight);

        return (left, top, right, bottom);
    }

    private static float Min4(float a, float b, float c, float d) => MathF.Min(MathF.Min(a, b), MathF.Min(c, d));

    private static float Max4(float a, float b, float c, float d) => MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
}
