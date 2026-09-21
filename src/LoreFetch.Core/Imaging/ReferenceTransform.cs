using OpenCvSharp;

namespace LoreFetch.Core.Imaging;

/// Reference-side pre-step -- CardSpotter steps 2-3, reference (index-build)
/// side ONLY. Blurring and downsampling the reference is what destroys the
/// high-frequency detail a webcam frame can never reproduce, which is what
/// lets a photo and a print render converge at `CardHasher`'s shared steps
/// 4-6 -- see CLAUDE.md "Identification". This exists exactly once; the
/// query side (`QueryTransform`) does NOT call it, by design, and never
/// should, even for "symmetry" -- blurring both sides equally would not make
/// a render and a photo converge, it would just delete the mechanism that
/// does.
public static class ReferenceTransform
{
    /// Step 3's target width. Upstream passes `INTER_AREA` as `resize`'s 4th
    /// positional argument, which is actually `fx` -- so upstream's own
    /// resize silently runs `INTER_LINEAR`. We use `INTER_AREA` here
    /// deliberately, because it is the correct filter for the ~5x downscale
    /// this is, not because upstream does -- our own two sides only need to
    /// agree with each other, not with CardSpotter's binaries.
    public const int TargetWidth = 96;

    /// `source` is a color (or already-grayscale) card render -- the index
    /// builder decodes a Scryfall image straight off disk. Returns a new
    /// single-channel Mat, `TargetWidth` px wide with height scaled to match
    /// (step 3), already blurred (step 2), ready for `CardHasher.Hash`. The
    /// caller owns and disposes the result.
    public static Mat Prepare(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
        {
            throw new ArgumentException("ReferenceTransform.Prepare: source image is empty.", nameof(source));
        }

        // Step 2: GaussianBlur 3x3, sigma=1 on BOTH axes, passed explicitly
        // rather than left at the (0, 0) default -- this is what forces
        // OpenCV's bit-exact fixed-point path instead of an IPP/HAL shortcut
        // that is not bit-exact across platforms. See CLAUDE.md "The one
        // gate that matters most".
        using var blurred = new Mat();
        Cv2.GaussianBlur(source, blurred, new Size(3, 3), sigmaX: 1, sigmaY: 1);

        // Step 3: resize to TargetWidth px wide, aspect preserved, INTER_AREA.
        var targetHeight = Math.Max(1, (int)MathF.Round(source.Rows * (TargetWidth / (float)source.Cols)));
        using var resized = new Mat();
        Cv2.Resize(blurred, resized, new Size(TargetWidth, targetHeight), 0, 0, InterpolationFlags.Area);

        // Step 3: ... then grayscale -- after the resize, matching upstream
        // (`CardData.cpp:130` then grayscale conversion) and matching the
        // step ordering in CLAUDE.md's table.
        return ToGray(resized);
    }

    private static Mat ToGray(Mat bgrOrGray)
    {
        if (bgrOrGray.Channels() == 1)
        {
            var copy = new Mat();
            bgrOrGray.CopyTo(copy);
            return copy;
        }

        var gray = new Mat();
        var code = bgrOrGray.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY;
        Cv2.CvtColor(bgrOrGray, gray, code);
        return gray;
    }
}
