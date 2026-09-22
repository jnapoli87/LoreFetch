using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Scanning;

/// Orders quads the way a person reads a grid — top row left-to-right, then
/// the next row down — so `Cohort` tiles match the physical layout the user
/// just placed on the table.
///
/// `ICardDetector` orders quads by descending AREA, and that ordering must
/// stay exactly as it is: it is what picks which N quads survive when more
/// than N are detected. Reading order is a purely presentational concern
/// layered on top, applied to the SURVIVORS only, after selection has
/// already happened — conflating the two would mean losing the
/// largest-N-by-area selection the moment several cards are near-equal in
/// area, which is the common case for a 3x3 of same-sized cards.
///
/// Coordinates are expected in POST-ROTATION frame coordinates — the same
/// space `ICardDetector`, `CardQuad` and the live preview all already use —
/// so the rows and columns this produces line up with what the user sees on
/// screen, without this type needing to know the frame's width, height or
/// rotation at all.
public static class QuadOrdering
{
    /// Returns `quads` re-ordered into reading order. 0 or 1 quads pass
    /// through unchanged — there is nothing to order.
    ///
    /// The algorithm:
    ///  1. Compute each quad's centroid (mean of its four corners) and
    ///     height (mean of its two side lengths — the same pairing
    ///     `CardQuad.AspectRatio` uses internally).
    ///  2. Sort by centroid Y.
    ///  3. Walk that Y-sorted list, growing the CURRENT row while a quad's
    ///     centroid Y stays within half the MEDIAN quad height of the row's
    ///     own running mean Y; otherwise start a new row. Because the input
    ///     is already Y-sorted, comparing only against the current row's
    ///     mean (never against every prior row) is sufficient — a quad that
    ///     breaks out of the current row can only start a new one, never
    ///     rejoin an earlier one.
    ///  4. Sort each row by centroid X, left to right, then concatenate the
    ///     rows top to bottom.
    ///
    /// Half the MEDIAN quad height is the threshold, not a fixed pixel
    /// value and not the MEAN: cards run about 483 px tall at the locked
    /// mount height, so that threshold comfortably absorbs the ~10 px gaps
    /// and few-degree skew a freehand-placed 3x3 produces, while staying
    /// narrow enough that two genuinely different rows never merge. The
    /// MEDIAN (rather than the mean) is what keeps a single oddly detected
    /// or partially occluded quad from dragging the threshold around: an
    /// outlier can move a mean arbitrarily far, but it can shift the median
    /// by at most one rank.
    public static IReadOnlyList<CardQuad> ReadingOrder(IReadOnlyList<CardQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);

        if (quads.Count <= 1)
        {
            return quads;
        }

        var items = quads
            .Select(q => (Quad: q, Centroid: Centroid(q), Height: Height(q)))
            .ToList();

        var medianHeight = Median(items.Select(i => i.Height));
        var rowThreshold = medianHeight / 2f;

        var sortedByY = items.OrderBy(i => i.Centroid.Y).ToList();

        var rows = new List<List<(CardQuad Quad, PointF2 Centroid, float Height)>>();
        foreach (var item in sortedByY)
        {
            if (rows.Count > 0)
            {
                var currentRow = rows[^1];
                var rowMeanY = currentRow.Average(r => r.Centroid.Y);
                if (MathF.Abs(item.Centroid.Y - rowMeanY) <= rowThreshold)
                {
                    currentRow.Add(item);
                    continue;
                }
            }

            rows.Add(new List<(CardQuad Quad, PointF2 Centroid, float Height)> { item });
        }

        var ordered = new List<CardQuad>(quads.Count);
        foreach (var row in rows)
        {
            ordered.AddRange(row.OrderBy(r => r.Centroid.X).Select(r => r.Quad));
        }

        return ordered;
    }

    private static PointF2 Centroid(CardQuad q) =>
        new(
            (q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f,
            (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    /// Mean of the left and right side lengths. Duplicated (rather than
    /// exposed as a `CardQuad.Height` property) because `Core/Abstractions`
    /// is frozen contract surface and out of scope for this change; this is
    /// plain geometry, not a second source of truth for anything the
    /// contract itself defines.
    private static float Height(CardQuad q) =>
        (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f;

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2f
            : sorted[mid];
    }
}
