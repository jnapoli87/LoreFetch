using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using OpenCvSharp;

namespace LoreFetch.Lab.Synthetic;

/// Tunable knobs for `SyntheticFrameGenerator.Generate`. Every field is a
/// named, explicit parameter rather than a literal buried in the method
/// body -- package B7's own brief requires "keystone amount, blur sigma,
/// noise sigma, JPEG quality" to be "explicit and testable" -- and
/// `Default` is the "documented default set that represents a plausible
/// 9.75-inch capture" the brief also asks for.
public sealed record SyntheticFrameOptions
{
    /// The simulated camera frame's own resolution -- 1920x1080, the C920's
    /// native mode (CLAUDE.md "Geometry": "px/inch = 1360 / height_inches"
    /// is itself derived from a 1920-wide sensor).
    public int FrameWidth { get; init; } = 1920;

    public int FrameHeight { get; init; } = 1080;

    /// Fraction of the downscaled card's OWN height that one long edge is
    /// inset by, simulating a camera axis that is not perfectly
    /// perpendicular to the mat. 0.04 is "mild" by construction: at the
    /// 9.75-inch default height the card is ~488px tall, so a 0.04 inset
    /// moves that edge's corners by ~20px each -- visible, but nowhere
    /// near the near-90-degree keystoning `ContourCardDetector`'s aspect
    /// filter would reject outright.
    public float KeystoneAmount { get; init; } = 0.04f;

    /// `Cv2.GaussianBlur`'s sigma, applied to the WHOLE composited frame
    /// (lens softness/defocus is a property of the whole optical path, not
    /// of the card patch alone) -- deliberately larger than
    /// `ReferenceTransform`'s pinned sigma=1 3x3 kernel, which is a hash
    /// pre-step with its own bit-exactness contract and must never be
    /// reused here for a different purpose.
    public double BlurSigma { get; init; } = 0.8;

    /// Standard deviation of the per-pixel Gaussian sensor noise added
    /// after blur -- see `SyntheticFrameSupport.AddGaussianNoise`.
    public double NoiseSigma { get; init; } = 6.0;

    /// JPEG re-encode quality (0-100) applied last, after blur and noise --
    /// matches the C920's own MJPG capture pipeline, which is a lossy
    /// per-frame JPEG, not a lossless readout.
    public int JpegQuality { get; init; } = 85;

    /// Mat background brightness the card is composited onto -- mid gray by
    /// default; `BareMatGenerator`'s light/mid/dark constants are the ones
    /// that matter for Risk 3, not this generator's own default.
    public byte MatBrightness { get; init; } = 170;

    /// Seeds every deterministic random draw this generator makes (mat
    /// texture noise and sensor noise) -- same seed, same pixels, on every
    /// machine; see `SyntheticFrameSupport`'s own doc comment for why that
    /// is a `System.Random` guarantee and not an OpenCV-RNG one.
    public int Seed { get; init; } = 1;

    /// The filter `Generate`'s own downscale (step 1: `sourceCardBgr` down
    /// to the camera-observed card size) uses. Defaults to `INTER_AREA` --
    /// **by choice, not by fidelity**, the same reasoning CLAUDE.md gives
    /// for `ReferenceTransform`'s own 96px resize: `INTER_AREA` is the
    /// correct filter for what is always a downscale here (a ~5.1x
    /// downscale from a 488px-wide Scryfall `normal` render at the 9.75in
    /// mount height), and upstream CardSpotter only ends up on
    /// `INTER_LINEAR` at its own equivalent step by accident, via a
    /// positional-argument bug (`CardData.cpp:130` passes `INTER_AREA` as
    /// `resize`'s `fx`, not its `interpolation`). Exposed as a real,
    /// documented knob rather than a literal buried in `Generate` because
    /// B5c is itself a filter-and-scale experiment -- a caller genuinely
    /// may want to sweep this. Changing it makes frames generated under
    /// the new value **not pixel-comparable** with ones generated before
    /// the change, the same caution CLAUDE.md gives for any resize filter
    /// in this codebase.
    public InterpolationFlags DownscaleInterpolation { get; init; } = InterpolationFlags.Area;

    public static SyntheticFrameOptions Default { get; } = new();
}

/// A generated frame plus the geometry it was generated FROM -- never
/// anything the hash pipeline itself produced. `ExpectedCardWidthPx`/
/// `ExpectedCardHeightPx` are the pre-keystone downscaled card size (CLAUDE.md
/// "px/inch = 1360 / height_inches" applied to the 2.5x3.5-inch card
/// footprint from CLAUDE.md's own Geometry table) -- exposed so a test can
/// pin the SIZE claim directly, independent of whether `ContourCardDetector`
/// happens to detect the composited result. See chaos case (d) in this
/// package's own test file for why that independence matters: a test that
/// only ever checks the detector's OWN output can't distinguish "the
/// generator computed the wrong size" from "the detector mis-measured a
/// correctly-sized card".
public sealed record SyntheticFrameResult(CameraFrame Frame, float ExpectedCardWidthPx, float ExpectedCardHeightPx);

/// Builds a camera-like `CameraFrame` from a source card render (a Scryfall
/// `normal` image on disk for real use; a procedural "card-like" Mat in CI,
/// per orchestration finding V13 -- neither this type nor its tests care
/// which). Package B7 (docs/stream-b-identification.md).
///
/// **This type produces a SCENE, never a hash and never a `RectifiedCard`.**
/// That is not a style choice -- it is the whole structural guard the
/// package brief calls for ("the generator must not become a third
/// transform" / "must not be a third transform", CLAUDE.md +
/// stream-b-identification.md B7): the only public output is a
/// `CameraFrame` containing a mat background with a keystoned, blurred,
/// noised, JPEG-compressed card composited into it at some position this
/// type never reveals. There is no overload that hands back a rectified
/// card or a hash. A caller that wants to know what card this frame shows
/// has exactly one route available: `ICardDetector.Detect` ->
/// `IRectifier.Rectify` -> `ICardIdentifier.Identify` -- the same three
/// calls the shipping scan pipeline makes. See
/// `SyntheticFrameGeneratorRoundTripTests` for the acceptance test this
/// exists to make possible, and its own chaos-test notes for what happens
/// when a test tries to shortcut around this.
///
/// Steps, all deliberately OUTSIDE CardSpotter's seven-step hash (CLAUDE.md
/// "Identification") -- nothing here touches `ReferenceTransform`,
/// `QueryTransform` or `CardHasher`:
///   1. Downscale `sourceCardBgr` (any size; a Scryfall `normal` render is
///      488x680) to the camera-observed card size at `heightInches`, via
///      `SyntheticFrameOptions.DownscaleInterpolation` (default
///      `INTER_AREA` -- the correct filter for what is always a downscale
///      at any realistic camera height; CLAUDE.md's own ladder tops out at
///      9.75" for the 3x3 grid, where a card is already smaller than
///      488x680, and a taller/farther camera only downscales further; see
///      that option's own doc comment for why it is a named knob rather
///      than a literal, and why swapping it is a real, tested risk rather
///      than a hypothetical one).
///   2. A mild keystone: `Cv2.WarpPerspective` with `InterpolationFlags.Linear`
///      and `BorderTypes.Replicate` -- the SAME interpolation and border
///      mode `PerspectiveRectifier` is pinned to (see that type's own doc
///      comment for why an unstated default is a silent-divergence risk),
///      reused here to avoid introducing a second, unstated choice.
///      Compositing which destination pixels are genuinely "inside the
///      warped card" uses a separately-filled polygon mask (the keystoned
///      quad's own corners), not the warp's border-fill -- `Replicate`
///      extends the card's own edge pixels outward rather than leaving
///      them black, so it cannot itself distinguish real card content from
///      filled padding.
///   3. Composite onto a textured mat background at the frame's own
///      resolution.
///   4. Whole-frame `GaussianBlur` (lens softness), Gaussian sensor noise,
///      then a JPEG encode/decode round trip -- in that order, matching
///      the order a real capture's own degradations actually compose
///      (defocus happens optically before the sensor reads noise, and the
///      encoder compresses whatever the sensor already captured).
public static class SyntheticFrameGenerator
{
    /// CLAUDE.md "Geometry": card footprint is 2.5in x 3.5in.
    public const float CardWidthInches = 2.5f;

    public const float CardHeightInches = 3.5f;

    /// CLAUDE.md "Geometry": "px/inch = 1360 / height_inches" for the C920
    /// (78-degree diagonal FOV on a 1920-wide, 16:9 sensor).
    public const float PxPerInchNumerator = 1360f;

    public static SyntheticFrameResult Generate(Mat sourceCardBgr, float heightInches, SyntheticFrameOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCardBgr);
        if (sourceCardBgr.Empty())
        {
            throw new ArgumentException("SyntheticFrameGenerator.Generate: source card image is empty.", nameof(sourceCardBgr));
        }

        if (heightInches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightInches), heightInches, "heightInches must be positive.");
        }

        options ??= SyntheticFrameOptions.Default;

        var pxPerInch = PxPerInchNumerator / heightInches;
        var cardWidthPx = Math.Max(2, (int)MathF.Round(CardWidthInches * pxPerInch));
        var cardHeightPx = Math.Max(2, (int)MathF.Round(CardHeightInches * pxPerInch));

        if (cardWidthPx >= options.FrameWidth || cardHeightPx >= options.FrameHeight)
        {
            throw new ArgumentException(
                $"SyntheticFrameGenerator.Generate: a card at {heightInches}in ({cardWidthPx}x{cardHeightPx}px) " +
                $"does not fit inside a {options.FrameWidth}x{options.FrameHeight} frame.",
                nameof(heightInches));
        }

        using var downscaled = new Mat();
        Cv2.Resize(sourceCardBgr, downscaled, new Size(cardWidthPx, cardHeightPx), 0, 0, options.DownscaleInterpolation);

        using var keystoned = ApplyKeystone(downscaled, options.KeystoneAmount, out var mask);
        using (mask)
        {
            using var frame = MakeMatBackground(options);

            var originX = (options.FrameWidth - cardWidthPx) / 2;
            var originY = (options.FrameHeight - cardHeightPx) / 2;
            using var region = new Mat(frame, new Rect(originX, originY, cardWidthPx, cardHeightPx));
            keystoned.CopyTo(region, mask);

            using var blurred = new Mat();
            Cv2.GaussianBlur(frame, blurred, new Size(0, 0), options.BlurSigma);

            SyntheticFrameSupport.AddGaussianNoise(blurred, options.NoiseSigma, options.Seed);

            using var compressed = SyntheticFrameSupport.JpegRoundTrip(blurred, options.JpegQuality);

            var cameraFrame = FrameMat.FromMat(compressed);
            return new SyntheticFrameResult(cameraFrame, cardWidthPx, cardHeightPx);
        }
    }

    /// Warps `card` by insetting its LEFT edge inward by `keystoneAmount *
    /// card.Rows` px at both corners -- an arbitrary but fixed choice of
    /// which edge tilts, "mild" because the inset is a small fraction of
    /// the card's own height. Returns the warped card (same size as `card`,
    /// `InterpolationFlags.Linear` + `BorderTypes.Replicate`, matching
    /// `PerspectiveRectifier` exactly) and, via `mask`, a same-size
    /// single-channel mask that is 255 inside the true keystoned
    /// quadrilateral and 0 everywhere else -- computed by directly filling
    /// the destination quad's own corner points, never by inspecting the
    /// warp's own border fill (see the type's own doc comment for why that
    /// distinction matters). The caller owns and disposes both.
    private static Mat ApplyKeystone(Mat card, float keystoneAmount, out Mat mask)
    {
        var width = card.Cols;
        var height = card.Rows;
        var inset = keystoneAmount * height;

        var srcCorners = new[]
        {
            new Point2f(0, 0),
            new Point2f(width - 1, 0),
            new Point2f(width - 1, height - 1),
            new Point2f(0, height - 1),
        };

        var dstCorners = new[]
        {
            new Point2f(0, inset),
            new Point2f(width - 1, 0),
            new Point2f(width - 1, height - 1),
            new Point2f(0, height - 1 - inset),
        };

        using var transform = Cv2.GetPerspectiveTransform(srcCorners, dstCorners);
        var warped = new Mat();

        // See the type's own doc comment: INTER_LINEAR + Replicate, the
        // exact interpolation and border mode `PerspectiveRectifier` pins
        // for its own warp, reused rather than restated with a different
        // choice.
        Cv2.WarpPerspective(card, warped, transform, card.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);

        mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        var maskPoints = dstCorners.Select(p => new Point((int)MathF.Round(p.X), (int)MathF.Round(p.Y))).ToArray();
        Cv2.FillConvexPoly(mask, maskPoints, Scalar.All(255));

        return warped;
    }

    /// A textured mat background at the frame's own resolution -- plain
    /// brightness plus Gaussian noise, same shape as `BareMatGenerator`'s
    /// backgrounds but not shared with it: this generator always composites
    /// a card on top, so its background's own contrast is not a Risk-3
    /// variable the way `BareMatGenerator`'s three named contrasts are.
    private static Mat MakeMatBackground(SyntheticFrameOptions options)
    {
        var mat = new Mat(options.FrameHeight, options.FrameWidth, MatType.CV_8UC3, Scalar.All(options.MatBrightness));
        SyntheticFrameSupport.AddGaussianNoise(mat, options.NoiseSigma / 2, options.Seed);
        return mat;
    }
}
