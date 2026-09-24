using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Core.Identification;

/// Query-side pre-step: grayscale ONLY -- no blur, no resize. The query
/// enters the shared pipeline at step 4 with an already-rectified 488x680
/// card (`IRectifier`'s job), so it has nothing left of steps 2-3 to do; see
/// DECISIONS.md "Identification", where steps 2-3 are marked reference-side
/// only. This exists exactly once. Do NOT add the reference side's blur or
/// 96px resize here, even for symmetry with `ReferenceTransform` -- the
/// asymmetry is the whole mechanism (see that type's own doc comment).
public static class QueryTransform
{
    /// Converts `card`'s pixels straight to grayscale, at the card's own
    /// canonical size -- no blur, no resize. The caller owns and disposes
    /// the result.
    public static Mat Prepare(RectifiedCard card)
    {
        using var color = ToMat(card);
        var gray = new Mat();
        var code = card.Layout == PixelLayout.Bgra32 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY;
        Cv2.CvtColor(color, gray, code);
        return gray;
    }

    /// Builds a color Mat straight from `card.Pixels`, at the canonical
    /// 488x680 size every `RectifiedCard` carries. Also used by the round-
    /// trip gate and the accuracy harness, which need the color card itself
    /// and not just its grayscale hash input. The caller owns and disposes
    /// the result.
    public static Mat ToMat(RectifiedCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var bytesPerPixel = card.Layout == PixelLayout.Bgra32 ? 4 : 3;
        var type = card.Layout == PixelLayout.Bgra32 ? MatType.CV_8UC4 : MatType.CV_8UC3;
        const int width = RectifiedCard.CanonicalWidth;
        const int height = RectifiedCard.CanonicalHeight;
        var rowBytes = width * bytesPerPixel;

        // RectifiedCard.Pixels is a plain managed ReadOnlyMemory<byte>, so
        // this normally recovers the backing array with no copy; the
        // fallback only fires for a memory that genuinely isn't array-backed.
        if (!MemoryMarshal.TryGetArray(card.Pixels, out var segment) || segment.Array is null)
        {
            var copy = card.Pixels.ToArray();
            segment = new ArraySegment<byte>(copy);
        }

        var mat = new Mat(height, width, type);
        for (var row = 0; row < height; row++)
        {
            var srcOffset = segment.Offset + (row * card.Stride);
            Marshal.Copy(segment.Array!, srcOffset, mat.Ptr(row), rowBytes);
        }

        return mat;
    }
}
