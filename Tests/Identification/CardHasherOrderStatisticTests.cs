using LoreFetch.Core.Identification;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// Dedicated tests for CardSpotter's two easiest-to-mis-port details: the
/// per-cell order statistic (`CellMedian`) and its tie-break (`SetsBit`).
/// Both are exercised twice -- once as pure functions, once end-to-end
/// through `HashIcon` on a hand-built 32x32 icon, per B1's "construct 32x32
/// inputs directly" requirement -- so a regression in the wiring between
/// them (not just the functions themselves) is caught too.
public class CardHasherOrderStatisticTests
{
    // --- CellMedian: the pure function ---

    [Fact]
    public void CellMedian_IsThirtyThirdSmallest_NotAverageOfTwoMiddleValues()
    {
        // 0..63 sorted ascending: the 33rd smallest (0-based index 32) is
        // 32. A textbook even-length median would average index 31 and 32
        // (31 and 32) to 31.5 -- a different value entirely, and one that
        // doesn't even fit in a byte-typed midpoint the way this cast would.
        var sorted = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        Assert.Equal((byte)32, CardHasher.CellMedian(sorted));
    }

    [Fact]
    public void CellMedian_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => CardHasher.CellMedian(new byte[63]));
        Assert.Throws<ArgumentException>(() => CardHasher.CellMedian(new byte[65]));
    }

    // --- SetsBit: the pure function ---

    [Theory]
    [InlineData(200, 150, true)]  // strictly greater than median -> always set
    [InlineData(100, 150, false)] // strictly less than median -> never set
    [InlineData(200, 200, true)]  // equal, median > 128 -> tie-break sets it
    [InlineData(129, 129, true)]  // equal, median just above 128 -> set
    [InlineData(128, 128, false)] // equal, median AT 128 (not > 128) -> clear
    [InlineData(50, 50, false)]   // equal, median <= 128 -> clear
    public void SetsBit_MatchesCardSpotterTieBreak(byte value, byte median, bool expected)
    {
        Assert.Equal(expected, CardHasher.SetsBit(value, median));
    }

    // --- End-to-end through HashIcon, on a hand-built 32x32 icon ---

    [Fact]
    public void HashIcon_OrderStatisticCell_SetsExactlyBitsAboveTheThirtyThirdSmallest()
    {
        // Cell (0,0): raster-order values 0..63, i.e. pixel i carries value
        // i. With no ties except pixel 32 == median (32) itself, the only
        // bits CardSpotter's rule sets are 33..63 (strictly greater than
        // the median) -- pixel 32 ties the median but the median (32) is
        // not > 128, so its own bit stays clear.
        using var icon = MakeUniformIcon(fill: 100); // background cells: uniform, ties the median everywhere
        SetCell(icon, gridRow: 0, gridCol: 0, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        var hash = CardHasher.HashIcon(icon);

        ulong expected = 0;
        for (var i = 33; i <= 63; i++)
        {
            expected |= 1UL << i;
        }

        Assert.Equal(expected, hash.Words[0]);

        // Every other cell is uniform 100 -- all pixels tie the median
        // (100), and 100 is not > 128, so every other word is exactly 0.
        // This isolates the assertion above to cell (0,0) alone.
        for (var cell = 1; cell < CardHash.WordCount; cell++)
        {
            Assert.Equal(0UL, hash.Words[cell]);
        }
    }

    [Fact]
    public void HashIcon_TieBreak_UniformCellAboveOneTwentyEight_SetsEveryBit()
    {
        using var icon = MakeUniformIcon(fill: 50); // background: median 50, not > 128 -> all clear
        SetCell(icon, gridRow: 1, gridCol: 2, Repeat((byte)200)); // median 200, every pixel ties it

        var hash = CardHasher.HashIcon(icon);

        var cellIndex = (1 * 4) + 2;
        Assert.Equal(ulong.MaxValue, hash.Words[cellIndex]);
    }

    [Fact]
    public void HashIcon_TieBreak_UniformCellAtOrBelowOneTwentyEight_ClearsEveryBit()
    {
        using var icon = MakeUniformIcon(fill: 50);
        SetCell(icon, gridRow: 2, gridCol: 3, Repeat((byte)128)); // boundary: exactly 128, not > 128

        var hash = CardHasher.HashIcon(icon);

        var cellIndex = (2 * 4) + 3;
        Assert.Equal(0UL, hash.Words[cellIndex]);
    }

    private static byte[] Repeat(byte value) => Enumerable.Repeat(value, 64).ToArray();

    private static Mat MakeUniformIcon(byte fill) =>
        new(CardHasher.IconSize, CardHasher.IconSize, MatType.CV_8UC1, Scalar.All(fill));

    private static void SetCell(Mat icon, int gridRow, int gridCol, IReadOnlyList<byte> rasterOrderValues)
    {
        const int cellSize = 8;
        var baseY = gridRow * cellSize;
        var baseX = gridCol * cellSize;

        for (var y = 0; y < cellSize; y++)
        {
            for (var x = 0; x < cellSize; x++)
            {
                icon.Set(baseY + y, baseX + x, rasterOrderValues[(y * cellSize) + x]);
            }
        }
    }
}
