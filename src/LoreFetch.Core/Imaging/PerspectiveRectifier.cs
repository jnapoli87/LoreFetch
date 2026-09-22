using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Core.Imaging;

/// `IRectifier`: CardSpotter step 1, query side -- perspective-correct a
/// detected `CardQuad` out of a `CameraFrame` into a canonical 488x680
/// `RectifiedCard`. See CLAUDE.md's seven-step table: this is the ONE step
/// that is pinned to `INTER_LINEAR` rather than `INTER_AREA`, because
/// `warpPerspective` does not support `INTER_AREA` at all -- OpenCV's own
/// docs list it as "not supported by this function" for both `warpAffine`
/// and `warpPerspective`. `ReferenceTransform`/`QueryTransform`/`CardHasher`
/// each use `INTER_AREA` at their own resizes; this type is the sole
/// exception, by contract rather than by oversight.
///
/// Pixel-centre convention (this type's own choice; nothing upstream or in
/// the contract pins it, so it is decided and documented once, here):
/// `quad`'s four corners map to the PIXEL CENTRES of the output's four
/// extreme corner pixels -- (0,0), (487,0), (487,679), (0,679) -- not to
/// the outer geometric edges of the 488x680 canvas (which would be (0,0),
/// (488,0), (488,680), (0,680)). Reasons:
///   - `CardQuad`'s own corners come from `ContourCardDetector`'s
///     `findContours`/`approxPolyDP`, which report integer PIXEL
///     coordinates -- i.e. already "pixel-centre" in the usual OpenCV sense
///     (a `Point(3, 4)` names pixel (3, 4), not a line at x=3). Mapping
///     those into an outer-edge destination convention would introduce a
///     systematic half-pixel-plus offset between what the detector found
///     and what the hash actually sees, for no benefit.
///   - `RectifiedCard.CanonicalWidth/Height` (488x680) is also
///     `ReferenceTransform`'s target geometry: a Scryfall `normal` render
///     IS 488x680 pixels, addressed (0,0)..(487,679). Pixel-centre mapping
///     keeps the query and reference sides addressing the same pixel grid
///     with no boundary convention mismatch between them.
///   - It is what a marker test can assert exactly: a marker painted at
///     source pixel (x, y) lands at output pixel (x', y') with no
///     sub-pixel rounding surprise at the canvas edge, which an outer-edge
///     mapping (488, 680 as literal transform targets, one column/row past
///     the last real pixel) would introduce at exactly the corners --
///     see `PerspectiveRectifierTests` for the marker-based verification.
public sealed class PerspectiveRectifier : IRectifier
{
    /// `warpPerspective` samples outside the source image at the extreme
    /// corners whenever the destination pixel-centre convention (see the
    /// type's own doc comment) asks for a source sample fractionally beyond
    /// the last row/column -- e.g. the bottom-right destination pixel centre
    /// (487, 679) inverse-maps to source pixel (quad.BR.X, quad.BR.Y)
    /// exactly, but neighbouring interpolation taps for the LINEAR filter
    /// can reach half a pixel further. `BORDER_REPLICATE` extends the
    /// source's own edge pixels outward for those taps, rather than
    /// `BORDER_CONSTANT`'s black fill, which would otherwise darken exactly
    /// the card's outer border -- the single most information-bearing
    /// region for `CardHasher`'s step-4 crop, whose region starts at row 0.
    private const BorderTypes Border = BorderTypes.Replicate;

    public RectifiedCard Rectify(CameraFrame frame, CardQuad quad)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var source = FrameMat.ToMat(frame);

        var srcPoints = new[]
        {
            new Point2f(quad.TL.X, quad.TL.Y),
            new Point2f(quad.TR.X, quad.TR.Y),
            new Point2f(quad.BR.X, quad.BR.Y),
            new Point2f(quad.BL.X, quad.BL.Y),
        };

        // Pixel-centre convention -- see the type's own doc comment.
        var dstPoints = new[]
        {
            new Point2f(0, 0),
            new Point2f(RectifiedCard.CanonicalWidth - 1, 0),
            new Point2f(RectifiedCard.CanonicalWidth - 1, RectifiedCard.CanonicalHeight - 1),
            new Point2f(0, RectifiedCard.CanonicalHeight - 1),
        };

        using var transform = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
        using var warped = new Mat();

        // Step 1: INTER_LINEAR explicitly -- warpPerspective does not
        // support INTER_AREA (CLAUDE.md's seven-step table; OpenCV's own
        // docs list it as unsupported for this function). Leaving the flag
        // at its default would also BE InterpolationFlags.Linear, but an
        // unstated default is exactly the silent-divergence vector the
        // round-trip gate exists to catch -- see CLAUDE.md "The one gate
        // that matters most" -- so it is passed explicitly here.
        Cv2.WarpPerspective(
            source,
            warped,
            transform,
            new Size(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight),
            InterpolationFlags.Linear,
            Border);

        var (pixels, stride, layout) = ToManagedPixels(warped, frame.Layout);
        return new RectifiedCard(pixels, stride, layout, quad);
    }

    /// Copies `warped`'s pixels into a fresh, tightly-packed managed byte
    /// array -- `RectifiedCard` is plain managed memory, deliberately NOT
    /// pooled (see its own doc comment), so this never reaches for
    /// `ArrayPool`. `preferredLayout` is the SOURCE frame's own layout
    /// (Bgr24 or Bgra32); `warped` was produced by `WarpPerspective` on a
    /// Mat with that same channel count (`FrameMat.ToMat` preserves it), so
    /// this only ever reports the layout that already matches the Mat's
    /// actual channel count -- it does not silently reinterpret one for
    /// the other.
    private static (byte[] Pixels, int Stride, PixelLayout Layout) ToManagedPixels(Mat warped, PixelLayout preferredLayout)
    {
        var channels = warped.Channels();
        var layout = channels == 4 ? PixelLayout.Bgra32 : PixelLayout.Bgr24;
        if (layout != preferredLayout && preferredLayout == PixelLayout.Bgra32 && channels != 4)
        {
            // Defensive only: FrameMat.ToMat always yields 3 or 4 channels
            // matching frame.Layout exactly, and WarpPerspective never
            // changes channel count, so this branch is unreachable in
            // practice -- kept as an explicit statement of the invariant
            // rather than a silent fallback to the wrong layout.
            throw new InvalidOperationException(
                $"PerspectiveRectifier: warped Mat has {channels} channel(s), expected 4 to match {preferredLayout}.");
        }

        var width = warped.Cols;
        var height = warped.Rows;
        var bytesPerPixel = channels;
        var stride = width * bytesPerPixel;

        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(warped.Ptr(row), pixels, row * stride, stride);
        }

        return (pixels, stride, layout);
    }
}
