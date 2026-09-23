using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.CropScale;

/// Package E1a: scales a detected `CardQuad` about its own centroid, along
/// its own (possibly rotated) width/height axes, independently -- the
/// pre-rectification analogue of `CropScaleTransform` (which scales an
/// already-rectified, axis-aligned canonical card, package B5c). This
/// exists because E1a's orchestrator finding is upstream of rectification:
/// on `tight_white` and the matching black-mat frame
/// (`normal_black_matches_tight_white`), the detector's own accepted quad
/// sits on the INNER edge of the card's black border -- the border itself
/// is cropped away and the coloured inner frame fills the crop -- exactly
/// the crop-scale defect CLAUDE.md's Risk 4 and B5c's curve describe, but
/// caught here at the quad, before `PerspectiveRectifier` ever runs, so the
/// expansion widens the SOURCE geometry rather than resampling an already-
/// canonical 488x680 card.
///
/// Diagnostic-only, `LoreFetch.Lab`-scoped, same status as
/// `CropScaleTransform` -- see that type's own doc comment for why B5c's
/// committed conclusion put this class of fix in Lab rather than
/// Core/Imaging (B5c's own 3-scale sweep was REJECTED for v1; this package
/// re-opens the question on DIFFERENT evidence -- a confirmed crop-inset
/// on this specific corpus -- but the ruling on where a fix ships, if any,
/// is still the user's, not this package's). `Core/Imaging` and
/// `Core/Scanning` are untouched by this file.
///
/// A quad from `ContourCardDetector` is not generally axis-aligned in frame
/// coordinates -- portrait cards, a keystoned camera angle, or freehand
/// placement all rotate or skew it -- so this cannot just scale X and Y
/// independently about the centroid (that would expand along the FRAME's
/// axes, not the CARD's own width/height axes, and would visibly skew a
/// rotated card outward rather than growing it uniformly along its own
/// edges). Instead it decomposes the quad as a parallelogram: for a
/// (near-)rectangular quad TL/TR/BR/BL,
///
///   TL = C - halfWidth - halfHeight
///   TR = C + halfWidth - halfHeight
///   BR = C + halfWidth + halfHeight
///   BL = C - halfWidth + halfHeight
///
/// where C is the centroid and halfWidth/halfHeight are the quad's own
/// (possibly non-orthogonal, for a slightly keystoned quad) half-edge
/// vectors, derived by averaging the two parallel edges. This is an EXACT
/// decomposition for a true parallelogram and a good approximation for the
/// near-parallelogram quads `ContourCardDetector` actually returns
/// (`approxPolyDP` output on a nearly-fronto-parallel photographed card).
/// Scaling halfWidth/halfHeight by `widthFactor`/`heightFactor`
/// independently and rebuilding the four corners from the SAME formula
/// then expands (factor &gt; 1) or shrinks (factor &lt; 1) the quad about its
/// own centroid, along its own width/height axes, regardless of rotation --
/// unlike scaling X/Y in frame coordinates, this is correct for the
/// portrait-oriented quads the 3x3-grid frames in this corpus contain.
public static class QuadExpansion
{
    /// `widthFactor`/`heightFactor` multiply the quad's own half-width/
    /// half-height vectors: 1.0 is a no-op (returns an equal quad, modulo
    /// floating-point rounding), &gt;1.0 grows the quad outward -- the
    /// correction this package's finding needs, since the detected quad is
    /// INSET from the true card edge -- and &lt;1.0 shrinks it. Both are
    /// independent so an anisotropic correction (e.g. the black border
    /// being proportionally different in width vs. height, as B5b/B5c
    /// measured for `plains_black`: ~5% width / ~3.5% height) can be
    /// modelled exactly, matching `CropScaleTransform`'s own width/height-
    /// independent design.
    public static CardQuad Expand(CardQuad quad, float widthFactor, float heightFactor)
    {
        if (widthFactor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(widthFactor), widthFactor, "Must be strictly positive.");
        }

        if (heightFactor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(heightFactor), heightFactor, "Must be strictly positive.");
        }

        // Exact identity short-circuit, bypassing the parallelogram
        // decomposition entirely: a real `ContourCardDetector` quad is only
        // an APPROXIMATE parallelogram (a slightly keystoned photograph, or
        // `approxPolyDP` corner noise, makes TR-TL and BR-BL not quite
        // equal), so reconstructing all four corners from a centroid + half-
        // width/half-height average -- even scaled by exactly 1.0 -- is a
        // SYMMETRIZING approximation, not a no-op: it can move a corner by a
        // pixel or two versus the original quad, which is enough to shift a
        // borderline hash distance. Skipping the whole computation at
        // (1, 1) makes "expand by a factor of 1.0" mean exactly what it
        // says -- the untouched, unapproximated original quad -- so a
        // caller comparing against factor 1.0 as a control is comparing
        // against the real shipped geometry, not a symmetrized copy of it.
        if (widthFactor == 1f && heightFactor == 1f)
        {
            return quad;
        }

        var centroid = Centroid(quad);

        // Exact for a parallelogram: each pair of opposite edges (top/bottom,
        // left/right) is parallel and equal-length, so averaging them halves
        // out `approxPolyDP`'s own per-corner noise rather than picking one
        // edge arbitrarily. Divided by 4 (not 2): the two summed vectors are
        // each a FULL width (or height) edge, so their average is a full
        // width/height vector, and a further /2 makes it a HALF vector.
        var halfWidth = Scale(Add(Sub(quad.TR, quad.TL), Sub(quad.BR, quad.BL)), 0.25f);
        var halfHeight = Scale(Add(Sub(quad.BL, quad.TL), Sub(quad.BR, quad.TR)), 0.25f);

        var scaledHalfWidth = Scale(halfWidth, widthFactor);
        var scaledHalfHeight = Scale(halfHeight, heightFactor);

        return new CardQuad(
            TL: Sub(Sub(centroid, scaledHalfWidth), scaledHalfHeight),
            TR: Sub(Add(centroid, scaledHalfWidth), scaledHalfHeight),
            BR: Add(Add(centroid, scaledHalfWidth), scaledHalfHeight),
            BL: Add(Sub(centroid, scaledHalfWidth), scaledHalfHeight));
    }

    private static PointF2 Centroid(CardQuad q) =>
        new((q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f, (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    private static PointF2 Add(PointF2 a, PointF2 b) => new(a.X + b.X, a.Y + b.Y);

    private static PointF2 Sub(PointF2 a, PointF2 b) => new(a.X - b.X, a.Y - b.Y);

    private static PointF2 Scale(PointF2 a, float s) => new(a.X * s, a.Y * s);
}
