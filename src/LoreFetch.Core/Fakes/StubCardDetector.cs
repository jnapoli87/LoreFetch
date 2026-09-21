using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Returns fixed quads laid out for 1, 3 or 9 cards, computed from the
/// frame's own dimensions so they always land inside the frame at any
/// resolution or orientation. How many it returns is configurable —
/// stream A drives all three layouts on demand, and the end-to-end suite
/// needs a partial cohort (any other count falls back to a generic grid).
public sealed class StubCardDetector : ICardDetector
{
    // 63:88 mm — real card proportions (long/short = 1.397).
    private const float CardShortMm = 63f;
    private const float CardLongMm = 88f;

    public StubCardDetector(int cardCount = 1)
    {
        CardCount = cardCount;
    }

    /// How many quads `Detect` proposes, before `maxCards` clamps it.
    public int CardCount { get; set; }

    /// Returns at most maxCards quads, ordered by descending area. Returns
    /// empty when CardCount (or maxCards) is zero or negative — never
    /// guesses.
    public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var count = Math.Min(CardCount, maxCards);
        if (count <= 0)
        {
            return Array.Empty<CardQuad>();
        }

        return BuildLayout(frame.Width, frame.Height, count);
    }

    private static IReadOnlyList<CardQuad> BuildLayout(int frameWidth, int frameHeight, int count)
    {
        var (rows, cols) = GridFor(count);

        // A margin around the whole grid, and a gutter between cells, so
        // quads never touch the frame edge or each other.
        const float marginFraction = 0.05f;
        const float gutterFraction = 0.08f;

        var marginX = frameWidth * marginFraction;
        var marginY = frameHeight * marginFraction;

        var cellWidth = (frameWidth - (2 * marginX)) / cols;
        var cellHeight = (frameHeight - (2 * marginY)) / rows;

        var cellInnerWidth = cellWidth * (1f - gutterFraction);
        var cellInnerHeight = cellHeight * (1f - gutterFraction);

        // Fit the card into the cell: start from the cell's width and derive
        // height from the real aspect ratio, then fall back to constraining
        // by height if that would overflow the cell vertically.
        var cardWidth = cellInnerWidth;
        var cardHeight = cardWidth * (CardLongMm / CardShortMm);
        if (cardHeight > cellInnerHeight)
        {
            cardHeight = cellInnerHeight;
            cardWidth = cardHeight * (CardShortMm / CardLongMm);
        }

        var quads = new List<CardQuad>(count);
        var placed = 0;
        for (var row = 0; row < rows && placed < count; row++)
        {
            for (var col = 0; col < cols && placed < count; col++)
            {
                var centerX = marginX + ((col + 0.5f) * cellWidth);
                var centerY = marginY + ((row + 0.5f) * cellHeight);

                var left = centerX - (cardWidth / 2f);
                var top = centerY - (cardHeight / 2f);
                var right = left + cardWidth;
                var bottom = top + cardHeight;

                quads.Add(new CardQuad(
                    TL: new PointF2(left, top),
                    TR: new PointF2(right, top),
                    BR: new PointF2(right, bottom),
                    BL: new PointF2(left, bottom)));

                placed++;
            }
        }

        return quads.OrderByDescending(q => q.AreaPx).ToList();
    }

    private static (int Rows, int Cols) GridFor(int count) => count switch
    {
        1 => (1, 1),
        3 => (1, 3), // 3 in a line
        9 => (3, 3),
        _ => GenericGrid(count),
    };

    private static (int Rows, int Cols) GenericGrid(int count)
    {
        var cols = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / cols);
        return (rows, cols);
    }
}
