using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Capture;

/// Exercises `JpegFrameDecoder` on synthetic JPEGs encoded in memory —
/// never image files, per CLAUDE.md's "never commit card imagery." Each
/// fixture is a procedurally drawn frame with a solid marker block in its
/// top-left quadrant against a contrasting background, so rotation can be
/// verified by asking "where did the marker end up," not just "is the
/// output the right size."
public class JpegFrameDecoderTests
{
    // BGR. Chosen far apart in all three channels so a lossy JPEG re-encode
    // (quality defaults are lossy) cannot plausibly blur one into the other
    // within the tolerance used below.
    private static readonly Scalar MarkerColorBgr = new(20, 20, 220); // red-ish
    private static readonly Scalar BackgroundColorBgr = new(220, 20, 20); // blue-ish

    private const int SourceWidth = 64;
    private const int SourceHeight = 48;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Decode_RotatesAndReportsGeometry_MarkerLandsInTheExpectedCorner(int rotationDegrees)
    {
        var jpegBytes = EncodeMarkerJpeg(SourceWidth, SourceHeight);
        var decoder = new JpegFrameDecoder(rotationDegrees);
        var jpegFrame = MakePooledJpegFrame(jpegBytes);

        using var frame = decoder.Decode(jpegFrame);

        Assert.NotNull(frame);

        // 90/270 swap the axes; 0/180 don't — this is the "1920x1080
        // becomes 1080x1920" claim from docs/design/capture.md's C3, checked
        // against a non-square fixture so a transpose bug can't hide
        // behind equal width and height.
        var (expectedWidth, expectedHeight) = rotationDegrees is 90 or 270
            ? (SourceHeight, SourceWidth)
            : (SourceWidth, SourceHeight);
        Assert.Equal(expectedWidth, frame!.Width);
        Assert.Equal(expectedHeight, frame.Height);

        // Rotating a photo clockwise walks a top-left marker clockwise
        // around the frame's own corners: TL -> TR -> BR -> BL as rotation
        // increases through 0/90/180/270. This is the convention this
        // decoder commits to (90 = Cv2.RotateFlags.Rotate90Clockwise, 270 =
        // Rotate90Counterclockwise) — swapping those two mappings is
        // exactly what the chaos test below re-applies.
        var expectedMarkerCorner = rotationDegrees switch
        {
            0 => Corner.TopLeft,
            90 => Corner.TopRight,
            180 => Corner.BottomRight,
            270 => Corner.BottomLeft,
            _ => throw new ArgumentOutOfRangeException(nameof(rotationDegrees)),
        };

        foreach (var corner in Enum.GetValues<Corner>())
        {
            var pixel = SampleCorner(frame, corner);
            if (corner == expectedMarkerCorner)
            {
                AssertColorClose(MarkerColorBgr, pixel);
            }
            else
            {
                AssertColorClose(BackgroundColorBgr, pixel);
            }
        }
    }

    [Fact]
    public void ComputeGeometry_SwapsAxesOnlyFor90And270()
    {
        Assert.Equal(new FrameGeometry(1920, 1080, 0), JpegFrameDecoder.ComputeGeometry(1920, 1080, 0));
        Assert.Equal(new FrameGeometry(1080, 1920, 90), JpegFrameDecoder.ComputeGeometry(1920, 1080, 90));
        Assert.Equal(new FrameGeometry(1920, 1080, 180), JpegFrameDecoder.ComputeGeometry(1920, 1080, 180));
        Assert.Equal(new FrameGeometry(1080, 1920, 270), JpegFrameDecoder.ComputeGeometry(1920, 1080, 270));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void Constructor_RejectsUnsupportedRotation(int rotationDegrees)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegFrameDecoder(rotationDegrees));
    }

    [Fact]
    public void Decode_CorruptBuffer_ReturnsNull_AndReturnsItsPoolBuffer_WithoutLeaking()
    {
        var pool = new CountingArrayPool();
        var decoder = new JpegFrameDecoder(rotationDegrees: 0);
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; // not a JPEG
        var jpegFrame = MakePooledJpegFrame(garbage, pool);

        var frame = decoder.Decode(jpegFrame);

        Assert.Null(frame);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public void Decode_Success_OutputBufferIsPooledAndReturnedOnDispose()
    {
        var outputPool = new CountingArrayPool();
        var jpegBytes = EncodeMarkerJpeg(SourceWidth, SourceHeight);
        var decoder = new JpegFrameDecoder(rotationDegrees: 0, pool: outputPool);
        var jpegFrame = MakePooledJpegFrame(jpegBytes);

        var frame = decoder.Decode(jpegFrame);
        Assert.NotNull(frame);
        Assert.Equal(1, outputPool.RentCount);
        Assert.Equal(0, outputPool.ReturnCount);

        frame!.Dispose();
        Assert.Equal(1, outputPool.ReturnCount);
    }

    private enum Corner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// A marker block filling the top-left quadrant, background elsewhere.
    /// JPEG-encoded in memory — Cv2.ImEncode, never a file on disk — so no
    /// raster ever touches the working tree.
    private static byte[] EncodeMarkerJpeg(int width, int height)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, BackgroundColorBgr);
        var marker = new Rect(0, 0, width / 2, height / 2);
        Cv2.Rectangle(mat, marker, MarkerColorBgr, thickness: -1);

        Cv2.ImEncode(".jpg", mat, out var jpegBytes);
        return jpegBytes;
    }

    private static PooledJpegFrame MakePooledJpegFrame(byte[] bytes, System.Buffers.ArrayPool<byte>? pool = null)
    {
        var effectivePool = pool ?? System.Buffers.ArrayPool<byte>.Shared;
        var buffer = effectivePool.Rent(bytes.Length);
        bytes.CopyTo(buffer, 0);
        return new PooledJpegFrame(buffer, bytes.Length, DateTimeOffset.UtcNow, effectivePool);
    }

    /// Samples a single pixel a quarter of the way in from the named
    /// corner — inset rather than the literal corner pixel, so JPEG block
    /// artifacts at the true edge can't flip an assertion.
    private static Vec3b SampleCorner(CameraFrame frame, Corner corner)
    {
        var insetX = frame.Width / 4;
        var insetY = frame.Height / 4;
        var (x, y) = corner switch
        {
            Corner.TopLeft => (insetX, insetY),
            Corner.TopRight => (frame.Width - 1 - insetX, insetY),
            Corner.BottomLeft => (insetX, frame.Height - 1 - insetY),
            Corner.BottomRight => (frame.Width - 1 - insetX, frame.Height - 1 - insetY),
            _ => throw new ArgumentOutOfRangeException(nameof(corner)),
        };

        var span = frame.Pixels.Span;
        var offset = (y * frame.Stride) + (x * 3);
        return new Vec3b(span[offset], span[offset + 1], span[offset + 2]);
    }

    /// JPEG is lossy, so "the same color" means "close," not "equal."
    private static void AssertColorClose(Scalar expectedBgr, Vec3b actualBgr, byte tolerance = 40)
    {
        Assert.True(
            Math.Abs(actualBgr.Item0 - expectedBgr.Val0) <= tolerance &&
            Math.Abs(actualBgr.Item1 - expectedBgr.Val1) <= tolerance &&
            Math.Abs(actualBgr.Item2 - expectedBgr.Val2) <= tolerance,
            $"expected BGR close to ({expectedBgr.Val0},{expectedBgr.Val1},{expectedBgr.Val2}), " +
            $"got ({actualBgr.Item0},{actualBgr.Item1},{actualBgr.Item2})");
    }
}
