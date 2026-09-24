using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// `QuadOrdering.ReadingOrder` is a pure function, so these are pure unit
/// tests: no frame, no pipeline. `ScanPipelineTests` carries the one
/// pipeline-level test that proves the pipeline actually calls this and
/// that `Cohort` tiles come out in the order it produces.
///
/// Card dimensions used throughout (346 x 483 px) match DECISIONS.md's locked
/// mount geometry — every card in every layout is 346 x 483 px at the
/// ~9.75" height a 3x3 requires — so these fixtures are the shape a real
/// freehand 3x3 actually produces, not an arbitrary test size.
public class QuadOrderingTests
{
    private const float CardWidth = 346f;
    private const float CardHeight = 483f;

    // -- Trivial pass-throughs -------------------------------------------

    [Fact]
    public void ReadingOrder_ZeroQuads_ReturnsEmpty()
    {
        var result = QuadOrdering.ReadingOrder(Array.Empty<CardQuad>());

        Assert.Empty(result);
    }

    [Fact]
    public void ReadingOrder_OneQuad_ReturnsItUnchanged()
    {
        var quad = MakeQuad(100f, 100f, CardWidth, CardHeight);

        var result = QuadOrdering.ReadingOrder(new[] { quad });

        Assert.Single(result);
        Assert.Equal(quad, result[0]);
    }

    // -- 3x3, shuffled/area-tied input, tight gaps ------------------------

    [Theory]
    [InlineData(10f)]
    [InlineData(6f)]
    public void ReadingOrder_3x3Grid_ShuffledInput_ComesOutRowMajor(float gapPx)
    {
        // Equal-sized quads mean "descending area" (ICardDetector's own
        // ordering) is not a well-defined order among them at all -- ties
        // break arbitrarily, which is exactly what a real freehand 3x3 of
        // same-sized cards produces. Shuffling models that.
        var rowMajor = BuildGrid(rows: 3, cols: 3, gap: gapPx);
        var shuffled = Shuffle(rowMajor, seed: 42);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(rowMajor, result);
    }

    // -- Skew + jitter must not perturb the order -------------------------

    [Fact]
    public void ReadingOrder_SkewAndJitterPerCard_DoesNotChangeOrder()
    {
        const int rows = 3;
        const int cols = 3;
        const float gap = 10f;
        var rng = new Random(2024); // fixed seed: deterministic test

        var indexed = new List<(int Index, CardQuad Quad)>();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var index = (r * cols) + c;
                var baseCenterX = 50f + (CardWidth / 2f) + (c * (CardWidth + gap));
                var baseCenterY = 50f + (CardHeight / 2f) + (r * (CardHeight + gap));

                var centerX = baseCenterX + NextJitterPx(rng);
                var centerY = baseCenterY + NextJitterPx(rng);
                var rotationDegrees = NextSkewDegrees(rng);

                indexed.Add((index, MakeQuad(centerX, centerY, CardWidth, CardHeight, rotationDegrees)));
            }
        }

        // Map each (perturbed, but still unique) quad back to the grid
        // index it was built from, so the assertion can check ORDER
        // without ReadingOrder itself needing to carry any identity.
        var indexByQuad = indexed.ToDictionary(i => i.Quad, i => i.Index);
        var shuffled = Shuffle(indexed.Select(i => i.Quad).ToList(), seed: 99);

        var result = QuadOrdering.ReadingOrder(shuffled);
        var resultIndices = result.Select(q => indexByQuad[q]).ToList();

        Assert.Equal(Enumerable.Range(0, rows * cols).ToList(), resultIndices);
    }

    // -- The threshold is keyed to the MEDIAN height, not the mean ---------

    [Fact]
    public void ReadingOrder_OneOversizedOutlierQuad_DoesNotSkewTheRowThreshold()
    {
        var rowMajor = BuildGrid(rows: 3, cols: 3, gap: 10f);

        // Replace the centre card with a wildly oversized quad (same
        // centroid, so its ROW membership is unaffected) to prove the row
        // threshold comes from the MEDIAN quad height. The other 8 heights
        // keep the median at exactly CardHeight regardless of this one
        // outlier; a MEAN-based threshold, by contrast, would be dragged
        // past the ~493 px row spacing by a single quad this tall (mean
        // over the 9 heights would land around 1096 px, so half of it,
        // 548 px, exceeds the row spacing and would merge all three rows
        // into one) — chaos case 4 in the package brief reproduces exactly
        // that and this test is what catches it.
        const int outlierIndex = 4;
        var outlierCenter = Centroid(rowMajor[outlierIndex]);
        var withOutlier = rowMajor.ToList();
        withOutlier[outlierIndex] = MakeQuad(outlierCenter.X, outlierCenter.Y, CardWidth, height: 6000f);

        var shuffled = Shuffle(withOutlier, seed: 55);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(withOutlier, result);
    }

    // -- 3-in-a-line, both orientations ------------------------------------

    [Fact]
    public void ReadingOrder_ThreeInARow_ComesOutLeftToRight()
    {
        var rowOf3 = BuildGrid(rows: 1, cols: 3, gap: 10f);
        var shuffled = Shuffle(rowOf3, seed: 7);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(rowOf3, result);
    }

    [Fact]
    public void ReadingOrder_ThreeInAColumn_ComesOutTopToBottom()
    {
        var columnOf3 = BuildGrid(rows: 3, cols: 1, gap: 10f);
        var shuffled = Shuffle(columnOf3, seed: 8);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(columnOf3, result);
    }

    // -- Partial capture keeps relative order ------------------------------

    [Fact]
    public void ReadingOrder_PartialCaptureOf7Of9_KeepsRelativeOrder()
    {
        var rowMajor = BuildGrid(rows: 3, cols: 3, gap: 10f);

        // Drop two non-adjacent cards (index 2, top-right, and index 6,
        // bottom-left) rather than a whole row or column, so this actually
        // exercises row grouping with holes in the input set -- a Space
        // capture of whatever happens to be on the table right now.
        var remainingIndices = new[] { 0, 1, 3, 4, 5, 7, 8 };
        var expected = remainingIndices.Select(i => rowMajor[i]).ToList();
        var shuffled = Shuffle(expected, seed: 13);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(expected, result);
    }

    // -- Real-capture regression: same-row Y jitter must not scramble X order ---

    /// **Real-capture regression.** Centroids and quad size for the first
    /// row are lifted directly from a real captured 3x3 frame, as pinned in
    /// stream B's independently-built inference at
    /// `Tests/Lab/Accuracy/SlotMapperTests.cs`
    /// (`SortRowMajor_RealCapturedTopRowWithYJitter_StillOrdersByXAscending`):
    /// three real detected centroids, (593,92), (839,99), (1109,96), each
    /// ~216x303 px -- card size at the ~15in height that frame was shot at,
    /// via DECISIONS.md's `px/inch = 1360/height_inches`. Y spans only 7 px
    /// across the row, which is enough to put the rightmost card ahead of
    /// the middle one under a strict `OrderBy(centroid Y).ThenBy(centroid X)`
    /// -- exactly the naive algorithm `ReadingOrder`'s own doc comment warns
    /// against. A synthetic grid with perfectly aligned rows (the tests
    /// above) cannot catch this; it only shows up when same-row centroids
    /// differ slightly in Y, which is every real frame. The second row
    /// (same source, its own small per-card jitter) proves the fix holds
    /// across more than one row, not just the one row pinned upstream.
    [Fact]
    public void ReadingOrder_RealCapturedRowsWithYJitter_OrdersLeftToRightThenTopToBottom()
    {
        var q1 = MakeQuad(593f, 92f, 216f, 303f);
        var q2 = MakeQuad(839f, 99f, 216f, 303f);
        var q3 = MakeQuad(1109f, 96f, 216f, 303f);
        var q4 = MakeQuad(593f, 400f, 216f, 303f);
        var q5 = MakeQuad(839f, 395f, 216f, 303f);
        var q6 = MakeQuad(1109f, 410f, 216f, 303f);

        var expected = new[] { q1, q2, q3, q4, q5, q6 };
        var shuffled = Shuffle(expected, seed: 71);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(expected, result);
    }

    // -- Short row (one missing card) must not disturb the other rows -----

    /// A single card missing from the MIDDLE row (index 4, the row's own
    /// centre) must not shift row 1 or row 3's members, and the now
    /// two-member middle row must not merge into a neighbouring row.
    /// `ReadingOrder_PartialCaptureOf7Of9_KeepsRelativeOrder` above drops
    /// one card from the first row and one from the last, leaving the
    /// middle row always full -- it never exercises a short MIDDLE row, so
    /// this test fills that gap. Mirrors stream B's own
    /// `SlotMapperTests.SortRowMajor_ShortRowMissingOneMember_DoesNotShiftOtherRows`,
    /// pinned here directly against `ReadingOrder`'s algorithm rather than
    /// `SlotMapper`'s.
    [Fact]
    public void ReadingOrder_ShortMiddleRow_KeepsRelativeOrder()
    {
        var rowMajor = BuildGrid(rows: 3, cols: 3, gap: 10f);

        // Drop index 4 -- the middle row's own middle card.
        var remainingIndices = new[] { 0, 1, 2, 3, 5, 6, 7, 8 };
        var expected = remainingIndices.Select(i => rowMajor[i]).ToList();
        var shuffled = Shuffle(expected, seed: 17);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(expected, result);
    }

    /// Same shape of gap, but in the FIRST row instead of the middle --
    /// proves a short row is handled at either end of the grid, not only
    /// when it has a full row on both sides to anchor against.
    [Fact]
    public void ReadingOrder_ShortFirstRow_KeepsRelativeOrder()
    {
        var rowMajor = BuildGrid(rows: 3, cols: 3, gap: 10f);

        // Drop index 1 -- the first row's own middle card.
        var remainingIndices = new[] { 0, 2, 3, 4, 5, 6, 7, 8 };
        var expected = remainingIndices.Select(i => rowMajor[i]).ToList();
        var shuffled = Shuffle(expected, seed: 23);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(expected, result);
    }

    // -- Rotated portrait geometry ------------------------------------------

    [Fact]
    public void ReadingOrder_RotatedPortraitGeometry_1080x1920_ComesOutRowMajor()
    {
        // Post-rotation frame is 1080 wide x 1920 tall -- the 1080/1920 axes
        // DECISIONS.md's locked mount produces for a 3x3 (1920 runs along the
        // table's depth). QuadOrdering takes no frame dimensions at all, so
        // this is really checking that nothing here silently assumes a
        // landscape frame; the fixture is sized to fit inside the stated
        // portrait frame as a sanity check on the test itself.
        var rowMajor = BuildGrid(rows: 3, cols: 3, cardWidth: 300f, cardHeight: 419f, gap: 10f, originX: 60f, originY: 60f);
        foreach (var quad in rowMajor)
        {
            Assert.True(quad.BR.X <= 1080f, "Test fixture must fit inside the stated 1080 px-wide frame.");
            Assert.True(quad.BR.Y <= 1920f, "Test fixture must fit inside the stated 1920 px-tall frame.");
        }

        var shuffled = Shuffle(rowMajor, seed: 21);

        var result = QuadOrdering.ReadingOrder(shuffled);

        Assert.Equal(rowMajor, result);
    }

    // -- Fixture builders --------------------------------------------------

    /// Row-major (index = row*cols + col) grid of card-sized quads with a
    /// gutter of `gap` px between neighbours, so callers can shuffle and
    /// still know what "correct" order looks like.
    private static List<CardQuad> BuildGrid(
        int rows, int cols, float gap, float cardWidth = CardWidth, float cardHeight = CardHeight,
        float originX = 50f, float originY = 50f)
    {
        var quads = new List<CardQuad>(rows * cols);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var centerX = originX + (cardWidth / 2f) + (c * (cardWidth + gap));
                var centerY = originY + (cardHeight / 2f) + (r * (cardHeight + gap));
                quads.Add(MakeQuad(centerX, centerY, cardWidth, cardHeight));
            }
        }

        return quads;
    }

    private static CardQuad MakeQuad(float centerX, float centerY, float width, float height, float rotationDegrees = 0f)
    {
        var hw = width / 2f;
        var hh = height / 2f;
        var corners = new (float X, float Y)[]
        {
            (-hw, -hh), // TL
            (hw, -hh),  // TR
            (hw, hh),   // BR
            (-hw, hh),  // BL
        };

        var rad = rotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);

        var pts = corners
            .Select(p => new PointF2(
                centerX + (p.X * cos) - (p.Y * sin),
                centerY + (p.X * sin) + (p.Y * cos)))
            .ToArray();

        return new CardQuad(pts[0], pts[1], pts[2], pts[3]);
    }

    /// Mirrors `QuadOrdering`'s own (private) centroid calculation, so a
    /// test can reposition a replacement quad at an existing quad's centre
    /// without duplicating full grid math.
    private static PointF2 Centroid(CardQuad q) =>
        new(
            (q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f,
            (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    private static float NextJitterPx(Random rng) => ((float)rng.NextDouble() * 10f) - 5f; // +/-5 px

    private static float NextSkewDegrees(Random rng) => ((float)rng.NextDouble() * 6f) - 3f; // +/-3 deg

    private static List<T> Shuffle<T>(IReadOnlyList<T> items, int seed)
    {
        var list = items.ToList();
        var rng = new Random(seed); // fixed seed: deterministic test
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
