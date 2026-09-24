using LoreFetch.Core.Abstractions;
using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// The H3 note block's two 🔴 requirements, pinned directly: row-major
/// centroid sort (requirement A), and count-mismatch never pairing by
/// position (requirement B) -- docs/history/orchestration-plan.md calls the second one
/// "the single highest-value test in the package."
///
/// Deliberately pure geometry: no `CameraFrame`, no OpenCV, no detector --
/// `SlotMapper` takes and returns plain `CardQuad` values, so these tests
/// can pin the algorithm exactly without the noise of a real image pipeline
/// (the end-to-end version, through a REAL detector, lives in
/// `AccuracyHarnessSyntheticTests`).
public class SlotMapperTests
{
    /// A 3x3 grid of axis-aligned quads at known centroids, fed to
    /// `SortRowMajor` in a SHUFFLED (not detection-area, not row-major)
    /// order -- proving the sort recovers row-major order from centroid
    /// position alone, independent of input order.
    [Fact]
    public void SortRowMajor_ThreeByThreeGridInShuffledOrder_RecoversRowMajorOrder()
    {
        // Slot layout (row, col), 1-based slot numbers:
        //   1 2 3
        //   4 5 6
        //   7 8 9
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);

        // Feed them in an order that is neither row-major nor area-descending
        // (StubCardDetector/ContourCardDetector's own contract) -- e.g.
        // slot 9, 1, 5, 3, 7, 2, 8, 4, 6.
        var shuffledSlotOrder = new[] { 9, 1, 5, 3, 7, 2, 8, 4, 6 };
        var input = shuffledSlotOrder.Select(slot => bySlot[slot]).ToList();

        var sorted = SlotMapper.SortRowMajor(input);

        for (var expectedSlot = 1; expectedSlot <= 9; expectedSlot++)
        {
            Assert.Equal(bySlot[expectedSlot], sorted[expectedSlot - 1]);
        }
    }

    /// The brief's own required case: quads that are ROTATED and OFFSET
    /// (not axis-aligned rectangles at all), fed in detection order (by
    /// area, largest first) rather than row-major -- proving the sort is
    /// governed by centroid position, not by shape, orientation, or the
    /// order the "detector" handed them in.
    [Fact]
    public void SortRowMajor_RotatedOffsetQuadsInDetectionOrder_StillSortsByCentroid()
    {
        // Three rotated parallelogram-ish quads whose CENTROIDS sit at
        // EXACTLY the same Y (one row, so the sort's secondary key -- X --
        // is what must resolve the order), but whose individual corners
        // are rotated/skewed by different amounts and areas so no single
        // CORNER coordinate would sort them correctly -- only the
        // centroid does. `SlotMapper.SortRowMajor` has no row-clustering
        // tolerance (by design -- see its own doc comment); it is a
        // strict two-key sort, so this is what "row-major" actually means
        // for a genuinely aligned row.
        var left = RotatedQuadAt(centerX: 100, centerY: 300, angleDegrees: 15, area: 30_000);
        var middle = RotatedQuadAt(centerX: 400, centerY: 300, angleDegrees: -20, area: 9_000); // smallest area
        var right = RotatedQuadAt(centerX: 700, centerY: 300, angleDegrees: 40, area: 20_000);

        // Detection order: descending area (ICardDetector's own contract),
        // which is left, right, middle -- NOT row-major (left, middle, right).
        var detectionOrder = new[] { left, right, middle };

        var sorted = SlotMapper.SortRowMajor(detectionOrder);

        Assert.Equal(new[] { left, middle, right }, sorted);
    }

    /// Chaos-test companion to the row-major test above: sorting by X
    /// first (or leaving detection order untouched) must NOT reproduce
    /// row-major order on a genuine multi-row grid -- confirms the test
    /// above is actually discriminating between algorithms, not merely
    /// checking a property every reasonable sort would satisfy. This is
    /// the brief's "break the row-major sort (e.g. sort by detection order,
    /// or by x before y) and prove a test fails" case, expressed as an
    /// assertion here rather than as a temporary source edit, so it stays
    /// in the suite as a permanent guard against the same regression.
    [Fact]
    public void SortRowMajor_IsNotEquivalentToSortingByXFirst_OnAMultiRowGrid()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var shuffled = new[] { 9, 1, 5, 3, 7, 2, 8, 4, 6 }.Select(slot => bySlot[slot]).ToList();

        var rowMajor = SlotMapper.SortRowMajor(shuffled);
        var xFirst = shuffled.OrderBy(CentroidX).ThenBy(CentroidY).ToList();

        Assert.NotEqual(rowMajor, xFirst);
    }

    /// **Live regression, found on a real captured 3x3 frame while this
    /// package was in review.** A strict `OrderBy(centroidY).ThenBy(centroidX)`
    /// -- the original implementation -- swaps slots 2 and 3 on exactly
    /// this row: three real detected centroids, Y values spanning only
    /// 7px (593,92), (839,99), (1109,96) sort under the naive rule to
    /// (593,92), (1109,96), (839,99). A synthetic grid with perfectly
    /// aligned rows (the tests above) cannot catch this -- it only shows up
    /// when same-row members differ slightly in Y, which is every real
    /// frame. Card size at 15in is ~216x303px (CLAUDE.md "px/inch =
    /// 1360/height_inches"), so these quads are built at that size.
    [Fact]
    public void SortRowMajor_RealCapturedTopRowWithYJitter_StillOrdersByXAscending()
    {
        var left = QuadAt(centerX: 593, centerY: 92, width: 216, height: 303);
        var middle = QuadAt(centerX: 839, centerY: 99, width: 216, height: 303);
        var right = QuadAt(centerX: 1109, centerY: 96, width: 216, height: 303);

        // Fed in an order that is not row-major either (matches how a real
        // detector's own area-descending contract could hand these back).
        var detectionOrder = new[] { right, left, middle };

        var sorted = SlotMapper.SortRowMajor(detectionOrder);

        Assert.Equal(new[] { left, middle, right }, sorted);
    }

    /// The chaos-test companion the coordinator's correction asked for
    /// explicitly, kept as a permanent assertion rather than only a
    /// temporary source edit: a full 3x3 grid where EVERY row has the same
    /// kind of small Y jitter as the real frame above (including one row
    /// where two members differ in Y from EACH OTHER by more than either
    /// differs from the next row), fed shuffled -- proves banding recovers
    /// full row-major order end to end, not just for one isolated row.
    [Fact]
    public void SortRowMajor_ThreeByThreeGridWithRealisticYJitterPerRow_RecoversRowMajorOrder()
    {
        // Row 1 (y ~90-99), row 2 (y ~395-410, one full card height + gutter
        // below row 1), row 3 (y ~700-712) -- each row's own members differ
        // from EACH OTHER by up to 15px (more than some cross-row gaps
        // would be if rows were packed tightly), which is exactly the case
        // a same-row-vs-different-row decision must get right.
        var q1 = QuadAt(593, 92, 216, 303);
        var q2 = QuadAt(839, 99, 216, 303);
        var q3 = QuadAt(1109, 96, 216, 303);
        var q4 = QuadAt(593, 400, 216, 303);
        var q5 = QuadAt(839, 395, 216, 303); // differs from q4 by 5, from q6 by 15
        var q6 = QuadAt(1109, 410, 216, 303);
        var q7 = QuadAt(593, 705, 216, 303);
        var q8 = QuadAt(839, 700, 216, 303);
        var q9 = QuadAt(1109, 712, 216, 303);

        var shuffled = new[] { q6, q1, q9, q4, q2, q8, q5, q7, q3 };

        var sorted = SlotMapper.SortRowMajor(shuffled);

        Assert.Equal(new[] { q1, q2, q3, q4, q5, q6, q7, q8, q9 }, sorted);
    }

    /// The coordinator's "does this compose with a short row" question,
    /// answered directly at the `SortRowMajor` level: an 8-of-9 grid
    /// missing slot 5 (row 2's middle member) still bands and orders the
    /// remaining 8 correctly -- the short row does not shift row 3's
    /// members into row 2's band, and does not shift any other row's
    /// members at all. (Whether an 8-of-9 COUNT is itself accepted is a
    /// separate question `TryMapToSlots` answers -- see its own tests; this
    /// is purely about whether `SortRowMajor`'s banding stays correct when
    /// handed an uneven row.)
    [Fact]
    public void SortRowMajor_ShortRowMissingOneMember_DoesNotShiftOtherRows()
    {
        var q1 = QuadAt(593, 92, 216, 303);
        var q2 = QuadAt(839, 99, 216, 303);
        var q3 = QuadAt(1109, 96, 216, 303);
        var q4 = QuadAt(593, 400, 216, 303);
        // q5 (839, ~400) is MISSING -- row 2 has only 2 members.
        var q6 = QuadAt(1109, 410, 216, 303);
        var q7 = QuadAt(593, 705, 216, 303);
        var q8 = QuadAt(839, 700, 216, 303);
        var q9 = QuadAt(1109, 712, 216, 303);

        var shuffled = new[] { q9, q4, q1, q6, q7, q3, q8, q2 };

        var sorted = SlotMapper.SortRowMajor(shuffled);

        Assert.Equal(new[] { q1, q2, q3, q4, q6, q7, q8, q9 }, sorted);
    }

    [Fact]
    public void TryMapToSlots_CountMatches_ReturnsRowMajorOrderedMapping()
    {
        var bySlot = BuildGrid(rows: 1, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var shuffled = new[] { 3, 1, 2 }.Select(slot => bySlot[slot]).ToList();

        var mapped = SlotMapper.TryMapToSlots(shuffled, expectedSlotCount: 3, out var orderedBySlot);

        Assert.True(mapped);
        Assert.Equal(bySlot[1], orderedBySlot![0]);
        Assert.Equal(bySlot[2], orderedBySlot[1]);
        Assert.Equal(bySlot[3], orderedBySlot[2]);
    }

    /// Requirement B, the package's single highest-value test: a 3x3 layout
    /// (9 expected) with only 8 quads detected (one missed card, matching
    /// B5a's own real-capture evidence of a sleeved card going undetected)
    /// must NOT return an ordered mapping at all -- there is no assignment
    /// a caller could receive here that "misaligns every slot after the
    /// gap", because none is ever produced.
    [Fact]
    public void TryMapToSlots_CountMismatch_ReturnsFalseAndNoMapping()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var eightOfNine = bySlot.Values.Take(8).ToList(); // one card missing -- count 8, expected 9

        var mapped = SlotMapper.TryMapToSlots(eightOfNine, expectedSlotCount: 9, out var orderedBySlot);

        Assert.False(mapped);
        Assert.Null(orderedBySlot);
    }

    [Fact]
    public void TryMapToSlots_ZeroDetections_ReturnsFalseAndNoMapping()
    {
        var mapped = SlotMapper.TryMapToSlots(Array.Empty<CardQuad>(), expectedSlotCount: 3, out var orderedBySlot);

        Assert.False(mapped);
        Assert.Null(orderedBySlot);
    }

    [Fact]
    public void TryMapToSlots_MoreDetectedThanExpected_ReturnsFalseAndNoMapping()
    {
        var bySlot = BuildGrid(rows: 1, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);

        var mapped = SlotMapper.TryMapToSlots(bySlot.Values.ToList(), expectedSlotCount: 2, out var orderedBySlot);

        Assert.False(mapped);
        Assert.Null(orderedBySlot);
    }

    /// Un-briefed chaos case of my own: a count that happens to match
    /// `expectedSlotCount` by coincidence, but where the detected quads are
    /// a DIFFERENT set than the ground truth expects (e.g. the same count,
    /// but genuinely different cards on the mat than were recorded). This
    /// is not something `SlotMapper` can detect on its own -- geometry
    /// carries no card identity -- but it documents the boundary of what
    /// "count matches" actually guarantees: `TryMapToSlots` returns a
    /// mapping whenever the COUNTS agree, and it is
    /// `AccuracyFrameRunner`/`ICardIdentifier.Identify` downstream that
    /// discovers a wrong card, not `SlotMapper` itself. Asserted here so a
    /// future change that tried to make `SlotMapper` "smarter" (e.g.
    /// silently dropping a slot it suspects is wrong) would be caught by
    /// this test expecting a full 9-quad mapping regardless.
    [Fact]
    public void TryMapToSlots_CountMatchesButContentUnknown_StillReturnsAFullMapping()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);

        var mapped = SlotMapper.TryMapToSlots(bySlot.Values.ToList(), expectedSlotCount: 9, out var orderedBySlot);

        Assert.True(mapped);
        Assert.Equal(9, orderedBySlot!.Count);
    }

    private static float CentroidX(CardQuad q) => (q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f;

    private static float CentroidY(CardQuad q) => (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f;

    /// Builds an axis-aligned `rows` x `cols` grid of card-shaped quads,
    /// each `cardWidth` x `cardHeight`, centered in its own `cellSize` x
    /// `cellSize` cell, keyed by 1-based ROW-MAJOR slot number -- the exact
    /// numbering convention `capture-fixtures.sh` uses ("the FIRST --card
    /// is slot 1 ... in slot order", laid out top-to-bottom-then-left-to-right).
    /// An axis-aligned card-shaped quad centered exactly at `(centerX,
    /// centerY)` -- used by the real-coordinate regression tests, where the
    /// centroid values themselves are the point (they come straight from a
    /// real capture) and rotation/offset is not what's under test.
    private static CardQuad QuadAt(float centerX, float centerY, float width, float height)
    {
        var left = centerX - (width / 2f);
        var top = centerY - (height / 2f);
        return new CardQuad(
            TL: new PointF2(left, top),
            TR: new PointF2(left + width, top),
            BR: new PointF2(left + width, top + height),
            BL: new PointF2(left, top + height));
    }

    private static Dictionary<int, CardQuad> BuildGrid(int rows, int cols, float cellSize, float cardWidth, float cardHeight)
    {
        var bySlot = new Dictionary<int, CardQuad>();
        var slot = 1;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var centerX = (col + 0.5f) * cellSize;
                var centerY = (row + 0.5f) * cellSize;
                var left = centerX - (cardWidth / 2f);
                var top = centerY - (cardHeight / 2f);

                bySlot[slot] = new CardQuad(
                    TL: new PointF2(left, top),
                    TR: new PointF2(left + cardWidth, top),
                    BR: new PointF2(left + cardWidth, top + cardHeight),
                    BL: new PointF2(left, top + cardHeight));
                slot++;
            }
        }

        return bySlot;
    }

    /// A quad whose CENTROID is exactly `(centerX, centerY)` but whose
    /// corners are rotated by `angleDegrees` about that centroid and sized
    /// to reach `area` -- deliberately not axis-aligned, so no individual
    /// corner's X or Y coordinate need bear any simple relationship to the
    /// centroid's own row/column.
    private static CardQuad RotatedQuadAt(float centerX, float centerY, float angleDegrees, float area)
    {
        var halfSize = MathF.Sqrt(area) / 2f;
        var angle = angleDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);

        PointF2 Rotate(float dx, float dy) => new(
            centerX + (dx * cos) - (dy * sin),
            centerY + (dx * sin) + (dy * cos));

        return new CardQuad(
            TL: Rotate(-halfSize, -halfSize),
            TR: Rotate(halfSize, -halfSize),
            BR: Rotate(halfSize, halfSize),
            BL: Rotate(-halfSize, halfSize));
    }
}

/// `TryInferGrid` -- package B6's Task 2 (docs/history/orchestration-plan.md): replaces
/// whole-frame dropping on a count mismatch with per-CELL grid inference,
/// so a 3x3 frame missing one card still classifies its other 8 (measured
/// on the real corpus: most frames find 8 of 9 -- see docs/accuracy.md).
/// Pure geometry, same style as `SlotMapperTests` above -- no detector, no
/// image.
public class SlotMapperTryInferGridTests
{
    [Fact]
    public void TryInferGrid_FullThreeByThreeGridShuffled_RecoversExactRowMajorPlacement()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var shuffled = new[] { 9, 1, 5, 3, 7, 2, 8, 4, 6 }.Select(slot => bySlot[slot]).ToList();

        var inferred = SlotMapper.TryInferGrid(shuffled, rows: 3, cols: 3, out var cells);

        Assert.True(inferred);
        for (var slot = 1; slot <= 9; slot++)
        {
            Assert.Equal(bySlot[slot], cells![slot - 1]);
        }
    }

    /// The package's headline case: a 3x3 frame missing its CENTER card
    /// (slot 5) still places the other 8 correctly, and slot 5's own cell
    /// comes back null rather than the whole frame failing to infer.
    [Fact]
    public void TryInferGrid_MissingCenterCell_PlacesEightAndLeavesCenterCellNull()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var eightOfNine = bySlot.Where(kv => kv.Key != 5).Select(kv => kv.Value).ToList();

        var inferred = SlotMapper.TryInferGrid(eightOfNine, rows: 3, cols: 3, out var cells);

        Assert.True(inferred);
        Assert.Null(cells![4]); // slot 5 -> index 4
        for (var slot = 1; slot <= 9; slot++)
        {
            if (slot == 5)
            {
                continue;
            }

            Assert.Equal(bySlot[slot], cells[slot - 1]);
        }
    }

    /// My own case, not named by the brief: proves the missing cell is
    /// found by POSITION regardless of where in the grid it falls, not
    /// just the middle -- a corner (slot 1, top-left) missing instead.
    /// A row/column-banding bug that only worked when the surviving
    /// members happened to still span every row/column edge-to-edge could
    /// pass the center case above and still fail here.
    [Fact]
    public void TryInferGrid_MissingCornerCell_PlacesEightAndLeavesThatCornerCellNull()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var eightOfNine = bySlot.Where(kv => kv.Key != 1).Select(kv => kv.Value).ToList();

        var inferred = SlotMapper.TryInferGrid(eightOfNine, rows: 3, cols: 3, out var cells);

        Assert.True(inferred);
        Assert.Null(cells![0]); // slot 1 -> index 0
        for (var slot = 2; slot <= 9; slot++)
        {
            Assert.Equal(bySlot[slot], cells[slot - 1]);
        }
    }

    [Fact]
    public void TryInferGrid_ZeroDetections_ReturnsFalse()
    {
        var inferred = SlotMapper.TryInferGrid(Array.Empty<CardQuad>(), rows: 3, cols: 3, out var cells);

        Assert.False(inferred);
        Assert.Null(cells);
    }

    /// The brief's explicit degenerate case: more detections than the
    /// layout's own cell count allows (the documented real desk-cable
    /// evidence in docs/accuracy.md's `solring_black` note) -- refused
    /// outright rather than guessing which detections are the real cards.
    [Fact]
    public void TryInferGrid_MoreDetectedThanCells_ReturnsFalse()
    {
        var bySlot = BuildGrid(rows: 1, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var plusOneExtra = bySlot.Values.Append(QuadAt(centerX: 2000, centerY: 2000, width: 40, height: 40)).ToList();

        var inferred = SlotMapper.TryInferGrid(plusOneExtra, rows: 1, cols: 3, out var cells);

        Assert.False(inferred);
        Assert.Null(cells);
    }

    /// The honest limitation documented on `TryInferGrid` itself: an
    /// ENTIRE row missing (as opposed to one cell within an otherwise
    /// intact row) cannot be told apart from "the grid only has 2 rows"
    /// without an absolute frame-position anchor this method does not
    /// have, so it refuses rather than guess which canonical row is gone.
    [Fact]
    public void TryInferGrid_EntireRowMissing_CannotConfidentlyInfer_ReturnsFalse()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var missingWholeMiddleRow = bySlot.Where(kv => kv.Key is not (4 or 5 or 6)).Select(kv => kv.Value).ToList();

        var inferred = SlotMapper.TryInferGrid(missingWholeMiddleRow, rows: 3, cols: 3, out var cells);

        Assert.False(inferred);
        Assert.Null(cells);
    }

    /// Distinguishes the row/column-band COLLISION path from the simpler
    /// "more detections than cells" overflow check above: here the
    /// detected count (9) does NOT exceed the grid's own cell count (9),
    /// but a spurious near-duplicate of slot 1's quad takes the place of
    /// the genuinely missing slot 9 -- band counts still come out exactly
    /// 3x3 (every row and column band is still populated), yet TWO quads
    /// resolve to the SAME cell (row 0, col 0). Must be refused rather
    /// than arbitrarily keeping one.
    [Fact]
    public void TryInferGrid_DuplicateQuadCollidesInAnAlreadyOccupiedCell_ReturnsFalse()
    {
        var bySlot = BuildGrid(rows: 3, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var eightOfNine = bySlot.Where(kv => kv.Key != 9).Select(kv => kv.Value).ToList();
        var duplicateOfSlotOne = QuadAt(centerX: 100 + 3, centerY: 100 + 2, width: 120, height: 170); // slot 1's own center is (100,100)

        var withCollision = eightOfNine.Append(duplicateOfSlotOne).ToList();

        var inferred = SlotMapper.TryInferGrid(withCollision, rows: 3, cols: 3, out var cells);

        Assert.False(inferred);
        Assert.Null(cells);
    }

    [Fact]
    public void TryInferGrid_SingleCardLayout_PlacesTheOneQuad()
    {
        var quad = QuadAt(centerX: 500, centerY: 400, width: 216, height: 303);

        var inferred = SlotMapper.TryInferGrid([quad], rows: 1, cols: 1, out var cells);

        Assert.True(inferred);
        Assert.Equal(quad, cells![0]);
    }

    [Fact]
    public void TryInferGrid_ThreeInARowFullCount_MatchesRowMajorOrder()
    {
        var bySlot = BuildGrid(rows: 1, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var shuffled = new[] { 3, 1, 2 }.Select(slot => bySlot[slot]).ToList();

        var inferred = SlotMapper.TryInferGrid(shuffled, rows: 1, cols: 3, out var cells);

        Assert.True(inferred);
        Assert.Equal(bySlot[1], cells![0]);
        Assert.Equal(bySlot[2], cells[1]);
        Assert.Equal(bySlot[3], cells[2]);
    }

    /// Layout 3's own version of the honest limitation: with only a
    /// single row, losing ANY one of the three cards loses that column's
    /// band entirely (no other row exists to keep it alive), so this
    /// falls back exactly like the whole-row case above. Never exercised
    /// by the real corpus (ground-truth.csv is all layout 9), but
    /// documented and tested rather than left as an assumption.
    [Fact]
    public void TryInferGrid_ThreeInARowOneMissing_CannotConfidentlyInfer_ReturnsFalse()
    {
        var bySlot = BuildGrid(rows: 1, cols: 3, cellSize: 200, cardWidth: 120, cardHeight: 170);
        var twoOfThree = bySlot.Where(kv => kv.Key != 2).Select(kv => kv.Value).ToList();

        var inferred = SlotMapper.TryInferGrid(twoOfThree, rows: 1, cols: 3, out var cells);

        Assert.False(inferred);
        Assert.Null(cells);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 1, 3)]
    [InlineData(9, 3, 3)]
    public void GridDimensionsForLayout_KnownLayouts_ReturnsExpectedShape(int layout, int expectedRows, int expectedCols)
    {
        var (rows, cols) = SlotMapper.GridDimensionsForLayout(layout);

        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedCols, cols);
    }

    /// Any layout outside {1, 3, 9} (never produced by the real app, but
    /// reachable from a hand-built or malformed ground-truth row) falls
    /// back to the same ceil(sqrt) generic grid
    /// `LoreFetch.Core.Fakes.StubCardDetector.GenericGrid` uses, rather
    /// than throwing -- e.g. layout 4 is a 2x2, matching
    /// `AccuracyHarnessSyntheticTests`'s own "layout claims one more card"
    /// test, which relies on layout 4 not throwing.
    [Fact]
    public void GridDimensionsForLayout_UnknownLayout_FallsBackToGenericSquareGrid()
    {
        var (rows, cols) = SlotMapper.GridDimensionsForLayout(4);

        Assert.Equal(2, rows);
        Assert.Equal(2, cols);
    }

    private static CardQuad QuadAt(float centerX, float centerY, float width, float height)
    {
        var left = centerX - (width / 2f);
        var top = centerY - (height / 2f);
        return new CardQuad(
            TL: new PointF2(left, top),
            TR: new PointF2(left + width, top),
            BR: new PointF2(left + width, top + height),
            BL: new PointF2(left, top + height));
    }

    private static Dictionary<int, CardQuad> BuildGrid(int rows, int cols, float cellSize, float cardWidth, float cardHeight)
    {
        var bySlot = new Dictionary<int, CardQuad>();
        var slot = 1;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var centerX = (col + 0.5f) * cellSize;
                var centerY = (row + 0.5f) * cellSize;
                bySlot[slot] = QuadAt(centerX, centerY, cardWidth, cardHeight);
                slot++;
            }
        }

        return bySlot;
    }
}
