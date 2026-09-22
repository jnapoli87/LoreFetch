using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.Accuracy;

/// The two requirements orchestration-plan.md's H3 note block flags as
/// "most likely to produce a silently wrong result" for B6, both enforced
/// structurally here rather than left to caller discipline:
///
/// **A. Quad -&gt; slot mapping is row-major by CENTROID, ROW-BANDED --
/// NOT a strict two-key (Y, then X) sort.** `ICardDetector.Detect` returns
/// quads "ordered by descending area" (its own contract) -- that is not
/// slot order, and nothing else in the pipeline imposes one. The operator
/// lays a physical grid out top-to-bottom-then-left-to-right and numbers
/// `--card` values the same way (`capture-fixtures.sh`).
///
/// A strict `OrderBy(centroidY).ThenBy(centroidX)` looks like the right
/// rule and is NOT: real hand-placed cards in one physical row never share
/// EXACTLY one Y value -- ordinary placement wobble and a degree of camera
/// tilt put same-row centroids a few pixels apart -- and a strict sort
/// interleaves rows the instant one row's Y range overlaps its neighbour's.
/// Confirmed on a real captured 3x3 frame (found live, not a hypothetical):
/// the true top row centred at (593,92), (839,99), (1109,96) -- Y values
/// spanning only 7px -- sorts under the naive rule to (593,92), (1109,96),
/// (839,99), swapping slots 2 and 3 on a frame where detection and
/// identification both worked. See `SlotMapperTests`'s own regression test
/// built from these exact coordinates.
///
/// The fix is to BAND rows before ordering within them: group quads whose
/// centroid Y falls within a tolerance of each other into the same row,
/// order rows top-to-bottom, then order each row's members left-to-right.
/// The tolerance is derived from the quads' OWN measured heights (real
/// cards, and therefore real row spacing, are far larger at 15in
/// (~216x303px) than at 20in (~165x235px) -- CLAUDE.md's "px/inch = 1360 /
/// height_inches" -- so a fixed pixel tolerance would be wrong at one
/// height or the other), at half a card height: real row spacing is about
/// one full card height, so half a card height bands same-row jitter
/// (single-digit to low-double-digit px in practice) while staying well
/// clear of the next row.
///
/// **B. A count mismatch must never be paired by position.** If
/// `Detect` finds a different number of quads than the ground-truth frame's
/// own `Layout`, zipping the two lists by index would silently misalign
/// every slot after the first gap -- B5a's own evidence (a sleeved card on
/// a dark mat went undetected) says this WILL happen on real captures, not
/// hypothetically. `TryMapToSlots` makes this structurally impossible to
/// get wrong: there is no code path that returns an ordered mapping when
/// the counts disagree. It returns `false` instead, and the caller
/// (`AccuracyFrameRunner`) is responsible for accounting for that frame in
/// its own category rather than guessing an assignment.
///
/// The two rules compose: a SHORT row (e.g. an 8-of-9 frame missing one
/// card) still bands and orders correctly on its own terms -- `SortRowMajor`
/// makes no assumption about how many members a row "should" have, so a
/// short row never shifts another row's members into the wrong band. That
/// count mismatch is still caught, separately, by `TryMapToSlots` at the
/// top level (8 != 9), which is what actually drops the frame; `SortRowMajor`
/// itself stays correct as a general-purpose sort regardless.
public static class SlotMapper
{
    public static IReadOnlyList<CardQuad> SortRowMajor(IReadOnlyList<CardQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count <= 1)
        {
            return quads.ToList();
        }

        var withMetrics = quads
            .Select(q => (Quad: q, CentroidX: CentroidX(q), CentroidY: CentroidY(q), Height: QuadHeight(q)))
            .OrderBy(m => m.CentroidY)
            .ToList();

        // Half a card height, derived from THIS frame's own detected quads
        // (median, so one anomalously small/large quad can't skew it) --
        // see this type's own doc comment for why a fixed pixel tolerance
        // cannot be right across both the 15in and 20in sweep heights.
        var tolerance = 0.5f * Median(withMetrics.Select(m => m.Height).ToList());

        var bands = new List<List<(CardQuad Quad, float CentroidX, float CentroidY, float Height)>>();
        foreach (var item in withMetrics)
        {
            var currentBand = bands.Count > 0 ? bands[^1] : null;
            if (currentBand is not null)
            {
                var bandAverageY = currentBand.Average(m => m.CentroidY);
                if (MathF.Abs(item.CentroidY - bandAverageY) <= tolerance)
                {
                    currentBand.Add(item);
                    continue;
                }
            }

            bands.Add([item]);
        }

        var result = new List<CardQuad>(quads.Count);
        foreach (var band in bands)
        {
            result.AddRange(band.OrderBy(m => m.CentroidX).Select(m => m.Quad));
        }

        return result;
    }

    /// Returns `true` and an `orderedBySlot` list of exactly
    /// `expectedSlotCount` quads (index 0 = slot 1, etc., via
    /// `SortRowMajor`) ONLY when `detectedQuads.Count == expectedSlotCount`.
    /// Otherwise returns `false` and a null list -- there is no partial or
    /// best-effort mapping. This is what makes "never pair by position on a
    /// count mismatch" a property of the TYPE rather than a rule a caller
    /// has to remember to apply: a caller cannot reach an ordered mapping
    /// through any path that skips this check.
    public static bool TryMapToSlots(
        IReadOnlyList<CardQuad> detectedQuads, int expectedSlotCount, out IReadOnlyList<CardQuad>? orderedBySlot)
    {
        ArgumentNullException.ThrowIfNull(detectedQuads);
        if (expectedSlotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSlotCount), expectedSlotCount, "expectedSlotCount must be positive.");
        }

        if (detectedQuads.Count != expectedSlotCount)
        {
            orderedBySlot = null;
            return false;
        }

        orderedBySlot = SortRowMajor(detectedQuads);
        return true;
    }

    /// The vertical extent of `q` in image coordinates -- average of its
    /// two "side" edges (TL-BL, TR-BR). Used only to derive the row-banding
    /// tolerance; it is deliberately NOT trying to be "the card's true
    /// physical height" (a card in a rotated/landscape layout might present
    /// its short edge vertically) -- any consistent per-quad vertical-pixel
    /// measure is sufficient for sizing a same-row tolerance from the
    /// quads actually present in this frame.
    private static float QuadHeight(CardQuad q) => (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f;

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static float Median(List<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    private static float CentroidX(CardQuad q) => (q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f;

    private static float CentroidY(CardQuad q) => (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f;
}
