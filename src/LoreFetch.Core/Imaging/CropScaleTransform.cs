using OpenCvSharp;

namespace LoreFetch.Core.Imaging;

/// Package B5c ONLY: synthesizes a crop-scale error onto an already-
/// canonical (488x680, or any other fixed size the caller passes) color
/// `Mat` -- this is not part of the shipping reference or query path (see
/// `ReferenceTransform`/`QueryTransform`'s own doc comments for why each of
/// those exists exactly once and unmodified). It exists so the crop-scale
/// experiment can ask "what would the query card have looked like if
/// `ContourCardDetector` had found a quad `insetFraction` off from the
/// card's true outer edge" without touching `ContourCardDetector` or
/// `PerspectiveRectifier` themselves.
///
/// Why this reproduces the real defect (B5a/B5b: a black-bordered card's
/// outer edge merges into a dark mat, so the detected quad lands inside the
/// true edge by roughly 5% per side in width, 3-4% in height):
/// `PerspectiveRectifier.Rectify` warps WHATEVER quad it is given to fill
/// the full canonical canvas, so an inset quad means the rectified card is
/// the true card's INTERIOR, stretched ("zoomed in") to fill the frame.
/// Cropping a smaller centred region out of a true (un-inset) render and
/// resizing it back up to the same canonical size reproduces exactly that.
///
/// A NEGATIVE fraction models the opposite error (a detected quad WIDER
/// than the true card, e.g. catching a sliver of mat): there are no real
/// pixels beyond a Scryfall render's own edge to sample, so this direction
/// is approximated by sampling a patch bigger than the source and letting
/// `Cv2.GetRectSubPix` replicate the boundary -- the SAME border
/// convention `PerspectiveRectifier` itself uses at the true card edge
/// (`BorderTypes.Replicate`; see that type's own doc comment), not a
/// second, differently-motivated choice.
///
/// `Cv2.GetRectSubPix` handles both directions with the same call: it
/// samples at fractional coordinates via bilinear interpolation (matching
/// `PerspectiveRectifier`'s own pinned `INTER_LINEAR`) and is documented to
/// replicate boundary pixels for any part of the requested patch that falls
/// outside the source image, provided the patch's centre stays inside it --
/// true here for every fraction this experiment actually uses (see the
/// range check below).
public static class CropScaleTransform
{
    /// `widthInsetFraction`/`heightInsetFraction` shrink (positive) or grow
    /// (negative) the effective source rectangle, per axis, independently --
    /// so the real observed defect (asymmetric: ~5% width, ~3-4% height)
    /// can be modelled exactly, not just an isotropic approximation of it.
    /// Both isotropic (equal on both axes) and anisotropic calls are valid;
    /// callers exploring "the curve" typically pass equal values, and the
    /// one real-condition point does not.
    public static Mat Apply(Mat canonicalBgr, float widthInsetFraction, float heightInsetFraction)
    {
        ArgumentNullException.ThrowIfNull(canonicalBgr);
        if (canonicalBgr.Empty())
        {
            throw new ArgumentException("CropScaleTransform.Apply: source image is empty.", nameof(canonicalBgr));
        }

        // Bounded so the virtual source rectangle can never collapse to
        // (or past) zero size in either axis -- an inset of 100% or more
        // has no meaningful "smaller region" to sample.
        if (widthInsetFraction <= -1f || widthInsetFraction >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthInsetFraction), widthInsetFraction, "Must be strictly between -1 and 1.");
        }

        if (heightInsetFraction <= -1f || heightInsetFraction >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heightInsetFraction), heightInsetFraction, "Must be strictly between -1 and 1.");
        }

        var width = canonicalBgr.Cols;
        var height = canonicalBgr.Rows;

        var srcWidth = Math.Max(1, (int)MathF.Round(width * (1f - widthInsetFraction)));
        var srcHeight = Math.Max(1, (int)MathF.Round(height * (1f - heightInsetFraction)));

        // Pixel-CENTRE convention, matching `PerspectiveRectifier`'s own
        // choice (see its doc comment): the image's centre sits at
        // ((width-1)/2, (height-1)/2), not (width/2, height/2) -- the
        // latter is half a pixel off for an even dimension and would make
        // even the zero-fraction (no-op) case fail to be a pixel-identical
        // round trip.
        var center = new Point2f((width - 1) / 2f, (height - 1) / 2f);

        using var patch = new Mat();
        Cv2.GetRectSubPix(canonicalBgr, new Size(srcWidth, srcHeight), center, patch);

        var resized = new Mat();
        Cv2.Resize(patch, resized, new Size(width, height), 0, 0, InterpolationFlags.Linear);
        return resized;
    }
}
