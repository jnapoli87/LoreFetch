using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Scanning;

/// Scales a detected `CardQuad` about its own centroid, along its own
/// (possibly rotated) width/height axes, independently. Moved into
/// Core (now `Core/Scanning`) from `LoreFetch.Lab.CropScale` (package DH) because
/// package E1a's finding is no longer diagnostic-only: on `tight_white` and
/// the matching black-mat frame, the detector's own accepted quad sits on
/// the INNER edge of the card's black border -- the border itself is
/// cropped away and the coloured inner frame fills the crop -- exactly the
/// crop-scale defect CLAUDE.md's Risk 4 and B5c's curve describe, caught
/// here at the quad, before `PerspectiveRectifier` ever runs. Package DH's
/// dual-hypothesis identification (`Core/Scanning/DualHypothesisIdentification`)
/// uses this type's <see cref="BorderWidthCorrectionFactor"/>/
/// <see cref="BorderHeightCorrectionFactor"/> to build its second, expanded
/// hypothesis, so it ships in the product assembly rather than only
/// `LoreFetch.Lab`.
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
/// portrait-oriented quads a 3x3-grid frame contains.
public static class QuadExpansion
{
    /// The measured black-border correction factor, width axis: growing a
    /// quad that landed exactly on the border's inner edge by this factor
    /// recovers the card's true outer edge. Measured over 40 real Scryfall
    /// `normal` renders (`BorderRatioMeasurement`, package E1a, non-land
    /// sample, seed 20260922, `stream/b` `4154a6b`) -- see
    /// `docs/history/orchestration-plan.md`'s 2026-09-22 "dual-hypothesis
    /// identification ships in v0.1.0" ruling for the full evidence
    /// (baseline 22/38, expanded-only 17/38, dual 37/38 non-land correct@1
    /// on the 15in integration corpus). This is the value
    /// `DualHypothesisIdentification` uses to build its expanded
    /// hypothesis; `LoreFetch.Lab`'s `expand-experiment` may still pass a
    /// DIFFERENT factor explicitly (or re-measure one from the cache) to
    /// explore the space this constant was chosen from.
    public const float BorderWidthCorrectionFactor = 1.085f;

    /// The measured black-border correction factor, height axis. Same
    /// measurement as <see cref="BorderWidthCorrectionFactor"/> -- see that
    /// constant's own doc comment.
    public const float BorderHeightCorrectionFactor = 1.089f;

    /// `widthFactor`/`heightFactor` multiply the quad's own half-width/
    /// half-height vectors: 1.0 is a no-op (returns an equal quad, modulo
    /// floating-point rounding), &gt;1.0 grows the quad outward -- the
    /// correction this package's finding needs, since the detected quad is
    /// INSET from the true card edge -- and &lt;1.0 shrinks it. Both are
    /// independent so an anisotropic correction (e.g. the black border
    /// being proportionally different in width vs. height, as B5b/B5c
    /// measured for `plains_black`: ~5% width / ~3.5% height) can be
    /// modelled exactly.
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
