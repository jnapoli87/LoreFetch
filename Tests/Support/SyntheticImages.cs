using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Tests.Support;

/// Deterministic, code-generated test images -- never a file on disk, so
/// there is no card-imagery risk here (CLAUDE.md "Never commit card
/// imagery"). These are procedural generators reused across B1's tests and
/// meant to stay reusable for B1b's golden-hash tests too, so keep their
/// signatures and RNG seeding stable rather than inlining pixel loops into
/// individual test methods.
internal static class SyntheticImages
{
    /// A "card-like" BGR image: a two-axis brightness gradient (standing in
    /// for a title bar fading into art) plus small per-pixel noise, so the
    /// image has genuine high-frequency content for the hash's per-cell
    /// median to react to -- a flat image would make every cell's bits
    /// degenerate to the tie-break alone. Deterministic in `seed`: same
    /// seed, same pixels, every run, on every machine.
    public static Mat MakeCardLikeBgr(int width, int height, int seed)
    {
        var stride = width * 3;
        var buffer = new byte[stride * height];
        var random = new Random(seed);

        for (var y = 0; y < height; y++)
        {
            var vertical = (y * 255) / Math.Max(1, height - 1);
            for (var x = 0; x < width; x++)
            {
                var horizontal = (x * 255) / Math.Max(1, width - 1);
                var noise = random.Next(-15, 16);
                var value = (byte)Math.Clamp(((vertical + horizontal) / 2) + noise, 0, 255);
                var offset = (y * stride) + (x * 3);

                buffer[offset] = value; // B
                buffer[offset + 1] = (byte)Math.Clamp(value + 10, 0, 255); // G
                buffer[offset + 2] = (byte)Math.Clamp(value - 10, 0, 255); // R
            }
        }

        return BgrFromBuffer(buffer, width, height, stride);
    }

    /// Independent per-pixel random noise -- deterministic in `seed`. Unlike
    /// a regular checkerboard (whose every downscale box averages to
    /// exactly the same value regardless of box-filter vs. bilinear
    /// sampling, degenerating to a flat image either way), true noise's
    /// local average genuinely differs between `INTER_AREA`'s box mean and
    /// `INTER_LINEAR`'s few-tap bilinear sample. This is what makes a
    /// resize-filter swap show up in a golden hash instead of disappearing
    /// into the same flat result under either filter.
    public static Mat MakeHighFrequencyNoiseBgr(int width, int height, int seed)
    {
        var stride = width * 3;
        var buffer = new byte[stride * height];
        var random = new Random(seed);
        random.NextBytes(buffer);

        return BgrFromBuffer(buffer, width, height, stride);
    }

    /// Alternating full-contrast pixels: the highest-frequency content a
    /// grayscale conversion can see, used to pin `QueryTransform` against
    /// any blur -- a 3x3 Gaussian blur collapses this pattern almost to a
    /// flat mid-grey, which is exactly what should make a divergence loud.
    public static Mat MakeHighFrequencyCheckerboardBgr(int width, int height)
    {
        var stride = width * 3;
        var buffer = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(((x + y) % 2 == 0) ? 255 : 0);
                var offset = (y * stride) + (x * 3);
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
            }
        }

        return BgrFromBuffer(buffer, width, height, stride);
    }

    /// A "card-like" BGR image that is exactly point-symmetric about its own
    /// center: pixel (x, y) and its mirror (width-1-x, height-1-y) always
    /// carry the identical BGR triplet. `Cv2.Flip(..., FlipMode.XY)` maps
    /// (x, y) to that same mirror position, so flipping this image (or any
    /// deterministic per-pixel function of it, such as a grayscale
    /// conversion) reproduces the identical Mat, byte for byte. B3b's
    /// non-orientation tests (ranking, distinctness, no-threshold, empty,
    /// catalog, Load) use this to make a query's upright and 180-degree
    /// hashes come out identical and known, without hand-deriving what
    /// pixel values would survive `CardHasher`'s crop/resize/median chain --
    /// see `HashCardIdentifierTests`'s own notes on why.
    public static Mat MakePointSymmetricBgr(int width, int height, int seed)
    {
        var stride = width * 3;
        var buffer = new byte[stride * height];
        var random = new Random(seed);
        var totalPixels = width * height;

        for (var index = 0; index < totalPixels; index++)
        {
            var mirrorIndex = totalPixels - 1 - index;
            if (mirrorIndex < index)
            {
                continue; // this pair (or the self-paired center pixel) was already written via its mirror
            }

            var b = (byte)random.Next(256);
            var g = (byte)random.Next(256);
            var r = (byte)random.Next(256);

            WritePixel(buffer, stride, width, index, b, g, r);
            WritePixel(buffer, stride, width, mirrorIndex, b, g, r);
        }

        return BgrFromBuffer(buffer, width, height, stride);
    }

    /// A global brightness offset, saturating at 0/255 like a real sensor's
    /// exposure shift would -- `Mat.ConvertTo`'s `beta` applies
    /// `saturate_cast<byte>(value + beta)` per channel.
    public static Mat AddBrightness(Mat bgr, double delta)
    {
        var result = new Mat();
        bgr.ConvertTo(result, MatType.CV_8UC3, alpha: 1.0, beta: delta);
        return result;
    }

    /// An order-preserving gamma curve: `v' = 255 * (v/255)^gamma`. Order-
    /// preserving means `v > m` survives for any two pixels, but the
    /// hash's tie-break tests the (already gamma'd) value against the fixed
    /// constant 128, so it is not gamma-invariant the way brightness is.
    public static Mat ApplyGamma(Mat bgr, double gamma)
    {
        using var lut = new Mat(1, 256, MatType.CV_8UC1);
        for (var i = 0; i < 256; i++)
        {
            var shifted = Math.Clamp(Math.Pow(i / 255.0, gamma) * 255.0, 0, 255);
            lut.Set(0, i, (byte)Math.Round(shifted));
        }

        var result = new Mat();
        Cv2.LUT(bgr, lut, result);
        return result;
    }

    /// Full inversion (`255 - v` per channel), which flips every non-tied
    /// bit comparison and is expected to land near, but not exactly at,
    /// 1024 bits of distance -- see `ReferenceTransformTests`.
    public static Mat Invert(Mat bgr)
    {
        var result = new Mat();
        Cv2.BitwiseNot(bgr, result);
        return result;
    }

    /// Wraps a canonical-size (488x680) BGR Mat as the `RectifiedCard` the
    /// contract hands `QueryTransform`, Bgr24, tightly packed.
    public static RectifiedCard ToRectifiedCard(Mat canonicalBgr)
    {
        if (canonicalBgr.Rows != RectifiedCard.CanonicalHeight || canonicalBgr.Cols != RectifiedCard.CanonicalWidth)
        {
            throw new ArgumentException(
                $"Expected a {RectifiedCard.CanonicalWidth}x{RectifiedCard.CanonicalHeight} BGR mat.", nameof(canonicalBgr));
        }

        var stride = RectifiedCard.CanonicalWidth * 3;
        var pixels = new byte[stride * RectifiedCard.CanonicalHeight];
        for (var row = 0; row < RectifiedCard.CanonicalHeight; row++)
        {
            Marshal.Copy(canonicalBgr.Ptr(row), pixels, row * stride, stride);
        }

        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(RectifiedCard.CanonicalWidth, 0),
            BR: new PointF2(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight),
            BL: new PointF2(0, RectifiedCard.CanonicalHeight));

        return new RectifiedCard(pixels, stride, PixelLayout.Bgr24, quad);
    }

    /// Pixel-exact equality for two single-channel Mats of the same size --
    /// used to pin a transform against a hand-built reference computation
    /// rather than tolerate any drift.
    public static bool GraysAreIdentical(Mat a, Mat b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols || a.Channels() != 1 || b.Channels() != 1)
        {
            return false;
        }

        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        return Cv2.CountNonZero(diff) == 0;
    }

    private static void WritePixel(byte[] buffer, int stride, int width, int linearIndex, byte b, byte g, byte r)
    {
        var y = linearIndex / width;
        var x = linearIndex % width;
        var offset = (y * stride) + (x * 3);
        buffer[offset] = b;
        buffer[offset + 1] = g;
        buffer[offset + 2] = r;
    }

    private static Mat BgrFromBuffer(byte[] buffer, int width, int height, int stride)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3);
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(buffer, row * stride, mat.Ptr(row), stride);
        }

        return mat;
    }
}
