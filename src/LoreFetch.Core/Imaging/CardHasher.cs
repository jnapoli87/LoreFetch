using OpenCvSharp;

namespace LoreFetch.Core.Imaging;

/// Steps 4-6 of the ported CardSpotter 1024-bit perceptual hash
/// (github.com/relgin/cardspotter, BSD-3-Clause, see THIRD-PARTY-NOTICES) --
/// the ONLY code shared between the reference (index-build) and query (scan)
/// sides. `ReferenceTransform` and `QueryTransform` each exist exactly once
/// and produce whatever grayscale image their own side calls for; this type
/// exists exactly once too and both sides call it on that output. See
/// CLAUDE.md "Identification" for why the two sides differ before this
/// point, and "The one gate that matters most" for why that split matters.
///
/// Two details here are easy to get wrong porting from a description rather
/// than the source, and both were checked against
/// `Code/CardData.h`/`CardData.cpp` in the upstream repo:
///   - CardSpotter's own `median()` is not the textbook median: for an
///     even-sized cell (every cell here is 8x8 = 64) it never averages the
///     two middle values -- it returns the plain upper order statistic, the
///     33rd smallest of 64 (`CellMedian` below). A port that reaches for a
///     library median helper lands half a level off.
///   - The bit test has a tie-break: a pixel exactly EQUAL to its cell's
///     median also sets the bit, but only when the median itself is bright
///     (> 128) (`SetsBit` below, `CardData.cpp:167`). Flat and blown-out
///     cells have many pixels equal to the median, so this is not a rare
///     branch -- dropping it (or inverting it) flips bits across most of a
///     uniformly-lit cell.
public static class CardHasher
{
    /// Step 5's target: the region resizes down to this square icon before
    /// the grid step. Matches `RectifiedCard`'s hash input geometry; not the
    /// canonical card size itself.
    public const int IconSize = 32;

    /// Step 4: the region is the top `width * RegionHeightFactor` of the
    /// card -- title, art and type line, ~61% of card height at the 1:1.397
    /// aspect ratio. `CardData.cpp:50`.
    private const float RegionHeightFactor = 0.85f;

    private const int GridSize = 4; // 4x4 grid
    private const int CellSize = IconSize / GridSize; // 8x8 px per cell
    private const int CellPixelCount = CellSize * CellSize; // 64
    private const int UpperMedianRank = CellPixelCount / 2; // 0-based index 32 = the 33rd smallest of 64

    /// Steps 4-6 end to end: the shared function both sides call. `gray`
    /// is whatever grayscale image the caller's own side-specific transform
    /// (`ReferenceTransform.Prepare` or `QueryTransform.Prepare`) produced --
    /// this type has no opinion on blur or the reference's 96px resize.
    public static CardHash Hash(Mat gray)
    {
        using var icon = ToIcon(gray);
        return HashIcon(icon);
    }

    /// Steps 4-5 alone: crop the top `width * 0.85` region and resize it to
    /// the canonical 32x32 icon (`INTER_AREA` -- the correct filter for what
    /// is always a downscale here, whether from a 96px-wide reference image
    /// or a 488px-wide rectified query card). Separated from `HashIcon` so
    /// callers -- and tests of the grid/median/tie-break step -- can supply
    /// an exact 32x32 icon directly, without a resize's interpolation
    /// touching the pixel values under test. The caller owns and disposes
    /// the result.
    public static Mat ToIcon(Mat gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (gray.Channels() != 1)
        {
            throw new ArgumentException("CardHasher.ToIcon expects a single-channel (grayscale) image.", nameof(gray));
        }

        if (gray.Empty())
        {
            throw new ArgumentException("CardHasher.ToIcon: source image is empty.", nameof(gray));
        }

        // Clamped to the image's own height so a source shorter than
        // 0.85 * its width -- shouldn't happen for a real card, but a
        // synthetic test image might be square or landscape -- never asks
        // for rows that don't exist.
        var regionHeight = Math.Clamp((int)MathF.Round(gray.Cols * RegionHeightFactor), 1, gray.Rows);
        using var region = new Mat(gray, new Rect(0, 0, gray.Cols, regionHeight));

        var icon = new Mat();
        Cv2.Resize(region, icon, new Size(IconSize, IconSize), 0, 0, InterpolationFlags.Area);
        return icon;
    }

    /// Step 6 alone: the 4x4 grid of 8x8 cells, each bit set by the cell's
    /// own upper order statistic and the CardSpotter tie-break. `icon` must
    /// already be `IconSize x IconSize`, single-channel -- exactly what
    /// `ToIcon` returns.
    public static CardHash HashIcon(Mat icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (icon.Channels() != 1 || icon.Rows != IconSize || icon.Cols != IconSize)
        {
            throw new ArgumentException(
                $"CardHasher.HashIcon expects a {IconSize}x{IconSize} single-channel image.", nameof(icon));
        }

        var words = new ulong[CardHash.WordCount];
        Span<byte> cellValues = stackalloc byte[CellPixelCount];
        Span<byte> sorted = stackalloc byte[CellPixelCount];

        for (var gridRow = 0; gridRow < GridSize; gridRow++)
        {
            for (var gridCol = 0; gridCol < GridSize; gridCol++)
            {
                var baseY = gridRow * CellSize;
                var baseX = gridCol * CellSize;

                for (var y = 0; y < CellSize; y++)
                {
                    for (var x = 0; x < CellSize; x++)
                    {
                        cellValues[(y * CellSize) + x] = icon.At<byte>(baseY + y, baseX + x);
                    }
                }

                cellValues.CopyTo(sorted);
                sorted.Sort();
                var median = CellMedian(sorted);

                ulong bits = 0;
                for (var i = 0; i < CellPixelCount; i++)
                {
                    if (SetsBit(cellValues[i], median))
                    {
                        bits |= 1UL << i;
                    }
                }

                words[(gridRow * GridSize) + gridCol] = bits;
            }
        }

        return new CardHash(words);
    }

    /// The cell's own upper order statistic: the 33rd smallest of 64 values
    /// (0-based index 32 once sorted ascending) -- NOT an average of the two
    /// middle values, which is what a library "median of an even-length
    /// sequence" helper would give. `sortedCellValues` must already be
    /// sorted ascending and contain exactly one 8x8 cell's worth of pixels.
    public static byte CellMedian(ReadOnlySpan<byte> sortedCellValues)
    {
        if (sortedCellValues.Length != CellPixelCount)
        {
            throw new ArgumentException(
                $"Expected {CellPixelCount} sorted cell values, got {sortedCellValues.Length}.", nameof(sortedCellValues));
        }

        return sortedCellValues[UpperMedianRank];
    }

    /// CardSpotter's exact bit test (`CardData.cpp:167`): strictly greater
    /// than the cell median sets the bit outright; equal to the median also
    /// sets it, but only when the median itself is bright (> 128). A dark
    /// median leaves an equal pixel's bit clear.
    public static bool SetsBit(byte value, byte median) =>
        value > median || (value == median && median > 128);
}
