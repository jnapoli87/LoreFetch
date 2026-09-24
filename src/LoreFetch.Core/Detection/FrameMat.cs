using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Core.Detection;

/// Converts between `CameraFrame` (the contract's plain-managed-memory
/// frame type) and an OpenCvSharp `Mat`, in both directions. Exists once so
/// `ContourCardDetector` and `LoreFetch.Lab`'s `detect` command (which loads
/// a still image straight into a Mat via `Cv2.ImRead` and needs to hand it
/// to `ICardDetector` as a `CameraFrame`) don't each grow their own copy of
/// this bookkeeping. Unrelated to `QueryTransform.ToMat`, which is
/// specifically about the fixed-size 488x680 `RectifiedCard` -- this type
/// is about the arbitrary-sized `CameraFrame` a detector actually sees.
public static class FrameMat
{
    /// Builds a color Mat straight from `frame.Pixels`. The caller owns and
    /// disposes the result.
    public static Mat ToMat(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bytesPerPixel = BytesPerPixel(frame.Layout);
        var type = frame.Layout == PixelLayout.Bgra32 ? MatType.CV_8UC4 : MatType.CV_8UC3;
        var rowBytes = frame.Width * bytesPerPixel;

        if (!MemoryMarshal.TryGetArray(frame.Pixels, out var segment) || segment.Array is null)
        {
            var copy = frame.Pixels.ToArray();
            segment = new ArraySegment<byte>(copy);
        }

        var mat = new Mat(frame.Height, frame.Width, type);
        for (var row = 0; row < frame.Height; row++)
        {
            var srcOffset = segment.Offset + (row * frame.Stride);
            Marshal.Copy(segment.Array!, srcOffset, mat.Ptr(row), rowBytes);
        }

        return mat;
    }

    /// Wraps a color Mat (3- or 4-channel, 8-bit) as a `CameraFrame`: a
    /// fresh, tightly-packed, unpooled buffer (pool: null, so `Dispose`
    /// never returns anything to a shared pool -- there is none here). Used
    /// by `LoreFetch.Lab`'s `detect` command, which reads a still image off
    /// disk rather than a live device.
    public static CameraFrame FromMat(Mat bgrOrBgra, DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(bgrOrBgra);
        if (bgrOrBgra.Channels() is not (3 or 4))
        {
            throw new ArgumentException("FrameMat.FromMat expects a 3- or 4-channel color Mat.", nameof(bgrOrBgra));
        }

        var layout = bgrOrBgra.Channels() == 4 ? PixelLayout.Bgra32 : PixelLayout.Bgr24;
        var bytesPerPixel = BytesPerPixel(layout);
        var width = bgrOrBgra.Cols;
        var height = bgrOrBgra.Rows;
        var stride = width * bytesPerPixel;

        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(bgrOrBgra.Ptr(row), pixels, row * stride, stride);
        }

        return new CameraFrame(pixels, width, height, stride, layout, capturedAt ?? DateTimeOffset.UtcNow, pool: null);
    }

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgr24 => 3,
        PixelLayout.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unsupported pixel layout."),
    };
}
