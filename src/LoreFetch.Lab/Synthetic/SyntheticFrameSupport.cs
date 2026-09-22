using System.Runtime.InteropServices;
using OpenCvSharp;

namespace LoreFetch.Lab.Synthetic;

/// Shared pixel-level helpers for `SyntheticFrameGenerator` and
/// `BareMatGenerator` -- deliberately NOT part of either type's public
/// surface, and deliberately NOT anything CardSpotter's ported pipeline
/// touches. Nothing here is a step of the seven-step hash (CLAUDE.md
/// "Identification"); it exists purely to make a generated frame LOOK like
/// a webcam capture before that frame is handed to the real
/// `ContourCardDetector` -> `PerspectiveRectifier` -> `CardHasher` path --
/// see `SyntheticFrameGenerator`'s own doc comment for why that boundary
/// matters.
///
/// Noise is added via a seeded `System.Random` and `Marshal.Copy` per row --
/// the same pattern `FrameMat`/`PerspectiveRectifier` already use to move
/// pixels in and out of a `Mat` -- rather than `Cv2.Randn`/`Cv2.Randu`.
/// OpenCV's RNG is not documented bit-exact across platforms (the same
/// concern CLAUDE.md raises for `INTER_AREA`, "Real risks" #2), and a
/// generator whose fixtures silently differ between the Mac and Windows
/// CI legs would be exactly the kind of platform-dependent flake this
/// project has already been burned by once. `System.Random` with an
/// explicit seed has no such ambiguity.
internal static class SyntheticFrameSupport
{
    /// Adds independent per-channel Gaussian noise (Box-Muller from a seeded
    /// `System.Random`), saturating at 0/255 like a real sensor's read noise
    /// would. `sigma &lt;= 0` is a no-op -- callers pass a generator's own
    /// `NoiseSigma` option straight through, and a caller that wants no
    /// noise at all (e.g. a chaos-test double-check) should not have to
    /// special-case zero.
    public static void AddGaussianNoise(Mat bgr, double sigma, int seed)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (sigma <= 0)
        {
            return;
        }

        var width = bgr.Cols;
        var height = bgr.Rows;
        var channels = bgr.Channels();
        var rowBytes = width * channels;
        var random = new Random(seed);
        var row = new byte[rowBytes];

        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(bgr.Ptr(y), row, 0, rowBytes);
            for (var i = 0; i < rowBytes; i++)
            {
                var noisy = row[i] + (sigma * NextGaussian(random));
                row[i] = (byte)Math.Clamp(Math.Round(noisy), 0, 255);
            }

            Marshal.Copy(row, 0, bgr.Ptr(y), rowBytes);
        }
    }

    /// Encodes `bgr` to JPEG at `quality` and immediately decodes it back --
    /// the standard way to bake real 8x8-DCT compression artifacts into a
    /// Mat without writing a temp file. The caller owns and disposes the
    /// result.
    public static Mat JpegRoundTrip(Mat bgr, int quality)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        Cv2.ImEncode(".jpg", bgr, out var encoded, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        var decoded = Cv2.ImDecode(encoded, ImreadModes.Color);
        if (decoded.Empty())
        {
            throw new InvalidOperationException("SyntheticFrameSupport.JpegRoundTrip: re-decoding the encoded JPEG produced an empty Mat.");
        }

        return decoded;
    }

    /// Box-Muller transform: one standard-normal sample per call, using two
    /// draws from `random`. `1.0 - NextDouble()` (rather than `NextDouble()`
    /// alone) keeps the log's argument in (0, 1] rather than [0, 1), so it
    /// never evaluates `Math.Log(0)`.
    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
