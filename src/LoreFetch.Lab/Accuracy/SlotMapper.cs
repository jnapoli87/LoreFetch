using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.Accuracy;

/// The two requirements docs/history/orchestration-plan.md's H3 note block flags as
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
            .ToList();

        // Half a card height, derived from THIS frame's own detected quads
        // (median, so one anomalously small/large quad can't skew it) --
        // see this type's own doc comment for why a fixed pixel tolerance
        // cannot be right across both the 15in and 20in sweep heights.
        var tolerance = 0.5f * Median(withMetrics.Select(m => m.Height).ToList());

        var bands = BandByTolerance(withMetrics, m => m.CentroidY, tolerance);

        var result = new List<CardQuad>(quads.Count);
        foreach (var band in bands)
        {
            result.AddRange(band.OrderBy(m => m.CentroidX).Select(m => m.Quad));
        }

        return result;
    }

    /// The (rows, cols) grid shape `TryInferGrid` and the app's own
    /// auto-capture "expected count" both use for a ground-truth `Layout`
    /// value -- mirrors `LoreFetch.Core.Fakes.StubCardDetector.GridFor`'s
    /// exact convention (1x1, 1x3, 3x3; anything else falls back to the
    /// same ceil(sqrt) generic grid) rather than inventing a second one,
    /// so a layout value means the same physical arrangement everywhere in
    /// the codebase that has to guess at one from a bare card count.
    public static (int Rows, int Cols) GridDimensionsForLayout(int layout) => layout switch
    {
        1 => (1, 1),
        3 => (1, 3), // 3 in a line
        9 => (3, 3),
        _ => GenericGridDimensions(layout),
    };

    private static (int Rows, int Cols) GenericGridDimensions(int count)
    {
        var cols = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / cols);
        return (rows, cols);
    }

    /// Infers a `rows` x `cols` grid over `detectedQuads` and places each
    /// one into the cell its centroid falls nearest, WITHOUT assuming the
    /// detected count equals `rows * cols` -- this is what lets a 3x3 frame
    /// missing one card (measured on the real corpus: 8 of 9 detected on
    /// most frames -- see docs/accuracy.md) still classify its other 8
    /// cells instead of the whole frame being dropped.
    ///
    /// Returns `true` and a row-major `cellsBySlot` of length `rows * cols`
    /// (index 0 = row 0/col 0, etc.) where a cell with no quad assigned is
    /// `null`, ONLY when the grid can be inferred with confidence. Returns
    /// `false` (and a null list) when it cannot -- there is no partial or
    /// best-effort GRID-STRUCTURE guess, mirroring `TryMapToSlots`'s own
    /// "refuse rather than guess" contract one level up:
    ///
    /// - Zero detections: no geometry to infer a pitch from at all.
    /// - More detections than the grid has cells: cannot be a well-formed
    ///   single-card-per-cell layout (a documented real case: a desk cable
    ///   passing the detector's aspect/area filters alongside real cards --
    ///   see docs/accuracy.md's `solring_black` note).
    /// - The number of distinct row-bands (by Y, same half-card-height
    ///   tolerance `SortRowMajor` uses) is not exactly `rows`, or the
    ///   number of distinct column-bands (by X) is not exactly `cols` --
    ///   this happens when an ENTIRE row or column has no detection at
    ///   all, which is NOT the same as one missing cell (a lone missing
    ///   cell leaves its row and column each with other members, so their
    ///   bands still exist) -- see this method's own tests. Disambiguating
    ///   "this row is entirely absent" from "the grid is only 2 rows"
    ///   needs an absolute frame-position anchor this method does not
    ///   have, so it refuses rather than guess which canonical row/column
    ///   is missing.
    /// - Two quads land in the same inferred cell (a row/column-band
    ///   collision) -- again the desk-cable case, when the spurious quad
    ///   happens to fall within an otherwise fully-populated row and
    ///   column band rather than forming its own.
    public static bool TryInferGrid(
        IReadOnlyList<CardQuad> detectedQuads, int rows, int cols, out IReadOnlyList<CardQuad?>? cellsBySlot)
    {
        ArgumentNullException.ThrowIfNull(detectedQuads);
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "rows must be positive.");
        }

        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "cols must be positive.");
        }

        cellsBySlot = null;
        var totalCells = rows * cols;

        if (detectedQuads.Count == 0 || detectedQuads.Count > totalCells)
        {
            return false;
        }

        var indexed = detectedQuads
            .Select((q, i) => (
                Index: i, Quad: q, CentroidX: CentroidX(q), CentroidY: CentroidY(q),
                Width: QuadWidth(q), Height: QuadHeight(q)))
            .ToList();

        var rowBands = BandByTolerance(indexed, m => m.CentroidY, 0.5f * Median(indexed.Select(m => m.Height).ToList()));
        if (rowBands.Count != rows)
        {
            return false;
        }

        var colBands = BandByTolerance(indexed, m => m.CentroidX, 0.5f * Median(indexed.Select(m => m.Width).ToList()));
        if (colBands.Count != cols)
        {
            return false;
        }

        var rowIndexByItem = new int[detectedQuads.Count];
        for (var r = 0; r < rowBands.Count; r++)
        {
            foreach (var item in rowBands[r])
            {
                rowIndexByItem[item.Index] = r;
            }
        }

        var colIndexByItem = new int[detectedQuads.Count];
        for (var c = 0; c < colBands.Count; c++)
        {
            foreach (var item in colBands[c])
            {
                colIndexByItem[item.Index] = c;
            }
        }

        var cells = new CardQuad?[totalCells];
        foreach (var item in indexed)
        {
            var cellIndex = (rowIndexByItem[item.Index] * cols) + colIndexByItem[item.Index];
            if (cells[cellIndex].HasValue)
            {
                // Row/column-band collision: two quads resolved to the same
                // cell. Cannot confidently tell which (if either) is the
                // real card -- refuse rather than pick one arbitrarily.
                return false;
            }

            cells[cellIndex] = item.Quad;
        }

        cellsBySlot = cells;
        return true;
    }

    /// Partitions `items` into contiguous bands along `axis`: sorts
    /// ascending, then walks the sorted list greedily, adding an item to
    /// the current band when it falls within `tolerance` of that band's
    /// running average, starting a new band otherwise. Shared by
    /// `SortRowMajor` (bands by Y only) and `TryInferGrid` (bands by Y AND,
    /// independently, by X) so both use exactly one banding algorithm.
    private static List<List<T>> BandByTolerance<T>(List<T> items, Func<T, float> axis, float tolerance)
    {
        var sorted = items.OrderBy(axis).ToList();
        var bands = new List<List<T>>();

        foreach (var item in sorted)
        {
            var currentBand = bands.Count > 0 ? bands[^1] : null;
            if (currentBand is not null)
            {
                var bandAverage = currentBand.Average(axis);
                if (MathF.Abs(axis(item) - bandAverage) <= tolerance)
                {
                    currentBand.Add(item);
                    continue;
                }
            }

            bands.Add([item]);
        }

        return bands;
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

    /// The horizontal extent of `q` -- average of its two "top"/"bottom"
    /// edges (TL-TR, BL-BR). Same reasoning as `QuadHeight`, used only to
    /// derive `TryInferGrid`'s column-banding tolerance.
    private static float QuadWidth(CardQuad q) => (Distance(q.TL, q.TR) + Distance(q.BL, q.BR)) / 2f;

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
