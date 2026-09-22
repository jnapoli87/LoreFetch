using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Unit tests for `PixelConvert` (A2) — the pure function the live preview
/// uses to turn a captured frame's raw bytes into the BGRA32 layout
/// `WriteableBitmap` expects. Both cases use a padded SOURCE stride and a
/// padded DESTINATION row (the destination is only ever guaranteed
/// `RowBytes >= Width * 4`, per `ILockedFramebuffer` — see
/// docs/stream-a-ui.md A1), and assert three things together: pixel values
/// land at the right offsets, the destination's own padding bytes are never
/// touched, and the output alpha byte is always 255.
public class PixelConvertTests
{
    private const byte DestinationSentinel = 0xAA;
    private const byte SourcePaddingFiller = 0xEE;

    [Fact]
    public void ToBgra32_Bgr24_RespectsPaddedSourceStrideAndPaddedDestinationRowBytes()
    {
        const int width = 3;
        const int height = 2;
        const int sourceStride = 12;         // width*3 = 9, padded by 3
        const int destinationRowBytes = 16;  // width*4 = 12, padded by 4

        // BGR triples per pixel; row padding filled with a value that must
        // never be read, let alone copied anywhere.
        byte[] source =
        [
            10, 20, 30, 40, 50, 60, 70, 80, 90, SourcePaddingFiller, SourcePaddingFiller, SourcePaddingFiller,
            11, 21, 31, 41, 51, 61, 71, 81, 91, SourcePaddingFiller, SourcePaddingFiller, SourcePaddingFiller,
        ];

        var destination = new byte[destinationRowBytes * height];
        Array.Fill(destination, DestinationSentinel);

        PixelConvert.ToBgra32(source, width, height, sourceStride, PixelLayout.Bgr24, destination, destinationRowBytes);

        // Row 0 pixels, in BGRA order, alpha forced to 255.
        Assert.Equal<byte>([10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255], destination[0..12]);
        // Row 0's own padding (bytes the pixel data never reaches) is untouched.
        Assert.Equal<byte>([DestinationSentinel, DestinationSentinel, DestinationSentinel, DestinationSentinel], destination[12..16]);

        // Row 1 pixels, landing at destinationRowBytes (16), not width*4 (12).
        Assert.Equal<byte>([11, 21, 31, 255, 41, 51, 61, 255, 71, 81, 91, 255], destination[16..28]);
        // Row 1's padding, likewise untouched.
        Assert.Equal<byte>([DestinationSentinel, DestinationSentinel, DestinationSentinel, DestinationSentinel], destination[28..32]);
    }

    [Fact]
    public void ToBgra32_Bgra32_RespectsPaddedStridesAndForcesAlphaTo255()
    {
        const int width = 3;
        const int height = 2;
        const int sourceStride = 16;         // width*4 = 12, padded by 4
        const int destinationRowBytes = 20;  // width*4 = 12, padded by 8
        const byte sourceAlpha = 77;         // deliberately not 255 — must be ignored

        byte[] source =
        [
            1, 2, 3, sourceAlpha, 4, 5, 6, sourceAlpha, 7, 8, 9, sourceAlpha, 0xDD, 0xDD, 0xDD, 0xDD,
            101, 102, 103, sourceAlpha, 104, 105, 106, sourceAlpha, 107, 108, 109, sourceAlpha, 0xDD, 0xDD, 0xDD, 0xDD,
        ];

        const byte destinationSentinel = 0xBB;
        var destination = new byte[destinationRowBytes * height];
        Array.Fill(destination, destinationSentinel);

        PixelConvert.ToBgra32(source, width, height, sourceStride, PixelLayout.Bgra32, destination, destinationRowBytes);

        Assert.Equal<byte>([1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255], destination[0..12]);
        Assert.Equal<byte>(
            [destinationSentinel, destinationSentinel, destinationSentinel, destinationSentinel,
             destinationSentinel, destinationSentinel, destinationSentinel, destinationSentinel],
            destination[12..20]);

        Assert.Equal<byte>([101, 102, 103, 255, 104, 105, 106, 255, 107, 108, 109, 255], destination[20..32]);
        Assert.Equal<byte>(
            [destinationSentinel, destinationSentinel, destinationSentinel, destinationSentinel,
             destinationSentinel, destinationSentinel, destinationSentinel, destinationSentinel],
            destination[32..40]);
    }
}
