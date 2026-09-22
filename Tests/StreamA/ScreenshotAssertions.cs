using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// Shared PNG-content assertions for the headless screenshot tests (A10-prep
/// item 1). A bare <c>Assert.NotNull(bitmap)</c> on the captured frame passes
/// just as happily for a correctly-sized but entirely blank surface as for a
/// real rendered window — which is exactly what a silently-broken render
/// path (a missing theme, an unstyled control, or headless drawing routed to
/// nothing) would still produce. These two checks — the saved PNG is the
/// window's own size, and its pixels are not all one colour — are what make
/// "a screenshot was produced" actually mean "the window rendered".
/// </summary>
internal static class ScreenshotAssertions
{
    /// <summary>
    /// Reloads the PNG from disk and asserts its pixel dimensions equal
    /// <paramref name="expectedWidth"/> x <paramref name="expectedHeight"/>.
    /// </summary>
    public static void AssertDimensions(string pngPath, int expectedWidth, int expectedHeight)
    {
        using var bitmap = new Bitmap(pngPath);
        Assert.Equal(expectedWidth, bitmap.PixelSize.Width);
        Assert.Equal(expectedHeight, bitmap.PixelSize.Height);
    }

    /// <summary>
    /// Reloads the PNG from disk and asserts its raw bytes are not all
    /// identical — a single flat colour is what an unrendered (but non-null,
    /// correctly-sized) surface would produce, and a bare existence/size
    /// check cannot tell the two apart.
    /// </summary>
    public static void AssertNotUniformColor(string pngPath)
    {
        using var bitmap = new Bitmap(pngPath);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4; // PngBitmapEncoderOptions.Default round-trips through Bgra8888
        var bufferSize = stride * size.Height;

        var handle = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle, bufferSize, stride);

            var buffer = new byte[bufferSize];
            Marshal.Copy(handle, buffer, 0, bufferSize);

            var first = buffer[0];
            var allSame = true;
            for (var i = 1; i < buffer.Length; i++)
            {
                if (buffer[i] != first)
                {
                    allSame = false;
                    break;
                }
            }

            Assert.False(
                allSame,
                $"Screenshot at {pngPath} is a single uniform byte value ({first}) across all " +
                $"{bufferSize} bytes — the window did not render real content.");
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }
    }
}
