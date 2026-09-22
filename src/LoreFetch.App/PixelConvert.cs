using LoreFetch.Core.Abstractions;

namespace LoreFetch.App;

/// Pure pixel-format conversion used by the live preview (A2) to turn a
/// captured frame's raw bytes into the BGRA32 layout Avalonia's
/// `WriteableBitmap` expects. `PixelLayout` has exactly two members
/// (CONTRACTS.md), and this switches on the frame's own layout rather than
/// assuming `Bgr24` — a `Bgra32` source (or `RectifiedCard.Layout`, later)
/// must not be treated as if it were three bytes per pixel.
///
/// Stateless and side-effect free: every byte it touches is named in a
/// parameter, which is what makes it testable without an Avalonia bitmap, a
/// camera, or a UI thread. Both `sourceStride` and `destinationRowBytes` are
/// respected explicitly rather than assumed to be `width * bytesPerPixel` —
/// the source may be padded by whatever produced it, and the destination
/// (an `ILockedFramebuffer`) is only ever guaranteed `RowBytes >= Width * 4`
/// (see docs/stream-a-ui.md A1). Camera frames carry no meaningful alpha
/// channel, so the output alpha byte is always forced to 255 regardless of
/// what a `Bgra32` source happens to carry there — callers must not rely on
/// a preserved source alpha.
internal static class PixelConvert
{
    public static void ToBgra32(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceStride,
        PixelLayout layout,
        Span<byte> destination,
        int destinationRowBytes)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        }

        var bytesPerSourcePixel = layout switch
        {
            PixelLayout.Bgr24 => 3,
            PixelLayout.Bgra32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown pixel layout."),
        };

        var sourceRowLength = width * bytesPerSourcePixel;
        var destinationRowLength = width * 4;

        if (sourceStride < sourceRowLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceStride), sourceStride, "Source stride is narrower than one row of pixels.");
        }

        if (destinationRowBytes < destinationRowLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationRowBytes), destinationRowBytes, "Destination row is narrower than one BGRA32 row.");
        }

        if (source.Length < (long)sourceStride * height)
        {
            throw new ArgumentException("Source span is shorter than sourceStride * height.", nameof(source));
        }

        if (destination.Length < (long)destinationRowBytes * height)
        {
            throw new ArgumentException(
                "Destination span is shorter than destinationRowBytes * height.", nameof(destination));
        }

        for (var y = 0; y < height; y++)
        {
            var sourceRow = source.Slice(y * sourceStride, sourceRowLength);
            var destinationRow = destination.Slice(y * destinationRowBytes, destinationRowLength);

            if (layout == PixelLayout.Bgr24)
            {
                for (var x = 0; x < width; x++)
                {
                    var s = x * 3;
                    var d = x * 4;
                    destinationRow[d + 0] = sourceRow[s + 0]; // B
                    destinationRow[d + 1] = sourceRow[s + 1]; // G
                    destinationRow[d + 2] = sourceRow[s + 2]; // R
                    destinationRow[d + 3] = 255;              // A — no source alpha to preserve
                }
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    var s = x * 4;
                    var d = x * 4;
                    destinationRow[d + 0] = sourceRow[s + 0]; // B
                    destinationRow[d + 1] = sourceRow[s + 1]; // G
                    destinationRow[d + 2] = sourceRow[s + 2]; // R
                    destinationRow[d + 3] = 255;              // A — forced opaque, source alpha ignored
                }
            }
        }
    }
}
