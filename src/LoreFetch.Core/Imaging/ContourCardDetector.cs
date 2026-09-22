using System.Runtime.CompilerServices;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

// Lets `Tests/StreamB` exercise `ContourCardDetector`'s internal seams
// directly (`OrderCorners`, `IsNestedOrDuplicate`, `DedupeAndTakeTopN`)
// without touching the frozen `LoreFetch.Core.csproj` -- this is a C#
// attribute, not a project-file edit. Same pattern as
// `LoreFetch.Lab.RepoPaths`'s own `InternalsVisibleTo` for the same reason.
[assembly: InternalsVisibleTo("LoreFetch.Tests.StreamB")]

namespace LoreFetch.Core.Imaging;

/// Why a contour was NOT turned into an accepted `CardQuad`. Exposed
/// structurally (not just logged) so `LoreFetch.Lab`'s `detect` command and
/// tests can act on it without scraping log text -- see package B5a.
public enum ContourRejectReason
{
    /// `approxPolyDP` did not collapse the contour to exactly 4 points.
    NotAQuad,

    /// The 4-point polygon is not convex (a real card's outline always is).
    NonConvex,

    /// `CardQuad.AreaPx` is below the active minimum-area threshold.
    MinArea,

    /// `CardQuad.AspectRatio` falls outside the active tolerance band around
    /// `ContourDetectorOptions.AspectRatioTarget`.
    AspectRatio,

    /// A corner sits within `BorderMarginPx` of the frame edge -- the card
    /// is (at least partially) off-frame, so its true extent is unknown.
    TouchesBorder,

    /// The quad's centroid falls inside an already-accepted, larger quad --
    /// almost always a card's own inner frame/art-box border re-detected as
    /// a second contour, not a second card.
    NestedDuplicate,

    /// The quad passed every other filter but ranked below the top
    /// `maxCards` by area.
    OverMaxCards,
}

/// One contour `Detect` looked at and did not accept, with enough geometry
/// to draw and enough detail to explain why -- both consumed by
/// `LoreFetch.Lab detect` and by tests, so this must stay data rather than
/// becoming a log-only side effect.
public sealed record RejectedContour(
    IReadOnlyList<PointF2> Points,
    ContourRejectReason Reason,
    string Detail,
    float AreaPx);

/// `Detect`'s full working, for anything that needs more than the accepted
/// list: `LoreFetch.Lab detect` draws both halves, and tests assert on
/// `Rejected` directly instead of scraping log output.
public sealed record DetectionDiagnostics(
    IReadOnlyList<CardQuad> Accepted,
    IReadOnlyList<RejectedContour> Rejected,
    bool UsedWidenedAspectTolerance);

/// Tunable knobs for `ContourCardDetector`. Every field has a default
/// grounded in CLAUDE.md's "Card detection" and "Geometry" sections; see
/// each field's own comment for the number behind it.
public sealed record ContourDetectorOptions
{
    /// 88/63 mm = 1.3968, CLAUDE.md's "Card detection".
    public float AspectRatioTarget { get; init; } = 88f / 63f;

    /// CLAUDE.md: "filter on aspect 1:1.397 ... ±15%".
    public float AspectTolerance { get; init; } = 0.15f;

    /// CLAUDE.md: "widening to ±25% if detection misses". Applied only as a
    /// same-frame retry when the strict pass accepts nothing at all -- see
    /// `ContourCardDetector.Detect`'s own comment.
    public float WidenedAspectTolerance { get; init; } = 0.25f;

    /// Minimum accepted quad area, as a fraction of the frame's own
    /// width*height, so the same default scales across resolutions instead
    /// of hard-coding a pixel count for 1920x1080 specifically.
    ///
    /// Justification (CLAUDE.md "Geometry" + this package's own ad-hoc
    /// frames): the SMALLEST real card this detector should ever have to
    /// accept is a single card at the tallest camera height the geometry
    /// table's ladder implies is still usable, roughly 20" -- px/inch =
    /// 1360/20 = 68, so a 2.5"x3.5" card is ~170x238 px = ~40,460 px^2, or
    /// ~1.95% of a 1920x1080 frame (2,073,600 px^2). The binding 3x3-grid
    /// mount height (9.75") gives ~346x483 = ~167,118 px^2 (~8.06%), and
    /// the ad-hoc fixtures at ~12" give ~260x370 = ~96,200 px^2 (~4.64%).
    /// 1% of the frame (~20,736 px^2 at 1080p, a ~144x144 patch) sits
    /// comfortably below all three real-card figures while still rejecting
    /// hand knuckles, sleeve glare spots and mat-seam fragments, which are
    /// characteristically much smaller than a whole card even at the
    /// farthest expected distance.
    public float MinAreaFraction { get; init; } = 0.01f;

    /// Overrides `MinAreaFraction` with an absolute pixel-area floor when
    /// set -- useful for `LoreFetch.Lab detect` against a still image whose
    /// resolution does not match a live 1920x1080 frame's implied geometry.
    public int? MinAreaPxOverride { get; init; }

    /// A quad with any corner within this many pixels of the frame edge is
    /// rejected as partially off-frame.
    public int BorderMarginPx { get; init; } = 2;

    public double CannyThreshold1 { get; init; } = 50;

    public double CannyThreshold2 { get; init; } = 150;

    /// Square structuring element side for the morphological close that
    /// bridges small gaps in the Canny edge map before contour-finding.
    public int MorphCloseKernelSize { get; init; } = 5;

    /// `findContours`'s retrieval mode. CLAUDE.md "Card detection" pins
    /// `RETR_EXTERNAL` -- a settled decision, not renegotiated here -- so
    /// the default MUST stay `RetrievalModes.External`. This knob exists
    /// only to make the alternative (`RetrievalModes.List`, which also
    /// returns nested/inner contours) measurable for the H1 "board outline
    /// nests the nine cards" investigation; see
    /// `LoreFetch.Lab RetrievalExperimentCommand` and docs/accuracy.md.
    /// `List` is expected to raise false positives from a card's own inner
    /// frame/art-box border re-detected as a second contour -- that is
    /// exactly what `DedupeAndTakeTopN`'s nested-duplicate check exists to
    /// suppress, and B5a recorded that check as dead code under
    /// `External` because `External` structurally never returns a nested
    /// contour in the first place. Switching to `List` is the one thing
    /// that can actually exercise it end-to-end.
    public RetrievalModes RetrievalMode { get; init; } = RetrievalModes.External;

    /// `approxPolyDP`'s epsilon, as a fraction of the contour's own
    /// perimeter -- the standard scale-independent way to pick it.
    public double ApproxPolyEpsilonFraction { get; init; } = 0.02;

    /// Rejected contours below this area (px^2) are counted but never drawn
    /// by `LoreFetch.Lab detect` -- pure sensor/JPEG noise, not worth ink.
    public float DiagnosticNoiseFloorPx { get; init; } = 64f;

    public static ContourDetectorOptions Default { get; } = new();
}

/// `ICardDetector`: `Canny` -> morphological close -> `findContours`
/// (`RETR_EXTERNAL`) -> `approxPolyDP` to 4 points -> corner ordering ->
/// aspect + minimum-area + border filters -> nested/duplicate dedupe -> top
/// N by area. See CLAUDE.md "Card detection" and "Geometry", and
/// docs/stream-b-identification.md §B5, for the algorithm and the
/// per-step rationale. Every discard reason is logged at Debug AND
/// returned structurally from `DetectWithDiagnostics` -- see
/// `RejectedContour`.
///
/// Corner ordering: `findContours`/`approxPolyDP` hand back 4 points in an
/// arbitrary cyclic start and (for a convex polygon) a consistent winding,
/// but with no notion of which corner is "top-left". This type recovers
/// TL/TR/BR/BL as follows, which is deliberately independent of the
/// quad's absolute orientation in the frame:
///   1. Sort the 4 points by angle about their centroid (ascending
///      `atan2`). In image coordinates (y grows downward), this always
///      produces a CLOCKWISE cyclic order -- the same winding as the real
///      corners TL, TR, BR, BL themselves -- regardless of which real
///      corner the sort happens to place first.
///   2. Classify the two pairs of opposite edges by length: the pair
///      averaging shorter is the card's ~63mm (width) edges, the other its
///      ~88mm (height) edges. Rotate the cyclic list (by 0 or 1 position)
///      so a SHORT edge sits between index 0 and 1.
///   3. Label index 0..3 as TL, TR, BR, BL.
/// Because step 2 only ever chooses between the two cyclic phases that put
/// a short edge first, the result is always either the true orientation or
/// its 180-degree rotation -- NEVER a 90-degree rotation and NEVER a
/// mirror (a mirror would reverse the clockwise winding step 1 already
/// fixed). This holds for a card lying "landscape" (long axis horizontal
/// in-frame) exactly as it does for one lying "portrait" and rotated up to
/// +-45 degrees: the classification is by edge LENGTH, never by which way
/// is "up" in the frame, so `IRectifier`'s warp (TL -> (0,0), TR ->
/// (488,0)) always produces a portrait 488x680 card. The remaining 180-
/// degree ambiguity ("is this card upside down") is a separate, accepted
/// problem -- CLAUDE.md's "Card orientation is unhandled" -- solved
/// downstream by hashing both orientations (B3b), not here.
public sealed class ContourCardDetector : ICardDetector
{
    private readonly ContourDetectorOptions _options;
    private readonly ILogger _logger;

    /// The fixed constructor package B5a's contract requires: works with
    /// just a logger, using every option's documented default.
    public ContourCardDetector(ILogger<ContourCardDetector> logger)
        : this(ContourDetectorOptions.Default, logger)
    {
    }

    public ContourCardDetector(ContourDetectorOptions options, ILogger<ContourCardDetector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = (ILogger?)logger ?? NullLogger<ContourCardDetector>.Instance;
    }

    /// `ICardDetector.Detect`: the accepted half of `DetectWithDiagnostics`.
    public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards) =>
        DetectWithDiagnostics(frame, maxCards).Accepted;

    /// Runs the full pipeline once at the strict `AspectTolerance`. If that
    /// pass accepts NOTHING and at least one contour was discarded only for
    /// its aspect ratio, retries the SAME frame at `WidenedAspectTolerance`
    /// (CLAUDE.md: "widening to ±25% if detection misses") and returns that
    /// pass instead when it finds something. Never widens when the strict
    /// pass already found cards -- widening only ever fires on an
    /// otherwise-empty result, so it can only ever turn "nothing" into
    /// "something," never change an already-successful read.
    public DetectionDiagnostics DetectWithDiagnostics(CameraFrame frame, int maxCards)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (maxCards <= 0)
        {
            return new DetectionDiagnostics(Array.Empty<CardQuad>(), Array.Empty<RejectedContour>(), false);
        }

        using var colorMat = FrameMat.ToMat(frame);
        using var gray = new Mat();
        Cv2.CvtColor(colorMat, gray, colorMat.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);

        var strict = RunPipeline(gray, frame.Width, frame.Height, maxCards, _options.AspectTolerance);
        if (strict.Accepted.Count > 0)
        {
            LogRejections(strict.Rejected);
            return strict;
        }

        var aspectRejectedAny = strict.Rejected.Any(r => r.Reason == ContourRejectReason.AspectRatio);
        if (!aspectRejectedAny)
        {
            LogRejections(strict.Rejected);
            return strict;
        }

        var widened = RunPipeline(gray, frame.Width, frame.Height, maxCards, _options.WidenedAspectTolerance);
        if (widened.Accepted.Count == 0)
        {
            LogRejections(strict.Rejected);
            return strict;
        }

        _logger.LogDebug(
            "Strict aspect tolerance ({Strict:P0}) found nothing; widened to {Widened:P0} and accepted {Count}.",
            _options.AspectTolerance,
            _options.WidenedAspectTolerance,
            widened.Accepted.Count);
        LogRejections(widened.Rejected);
        return widened with { UsedWidenedAspectTolerance = true };
    }

    private DetectionDiagnostics RunPipeline(Mat gray, int frameWidth, int frameHeight, int maxCards, float aspectTolerance)
    {
        var minAspect = _options.AspectRatioTarget * (1f - aspectTolerance);
        var maxAspect = _options.AspectRatioTarget * (1f + aspectTolerance);
        var minArea = _options.MinAreaPxOverride ?? (int)(frameWidth * (long)frameHeight * _options.MinAreaFraction);

        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        using var edges = new Mat();
        Cv2.Canny(blurred, edges, _options.CannyThreshold1, _options.CannyThreshold2);

        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(_options.MorphCloseKernelSize, _options.MorphCloseKernelSize));
        using var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(closed, out var contours, out _, _options.RetrievalMode, ContourApproximationModes.ApproxSimple);

        var rejected = new List<RejectedContour>();
        var candidates = new List<CardQuad>();

        foreach (var contour in contours)
        {
            var area = (float)Cv2.ContourArea(contour);
            var perimeter = Cv2.ArcLength(contour, true);
            var approx = Cv2.ApproxPolyDP(contour, _options.ApproxPolyEpsilonFraction * perimeter, true);

            if (approx.Length != 4)
            {
                rejected.Add(new RejectedContour(
                    ToPointFs(approx), ContourRejectReason.NotAQuad, $"approxPolyDP -> {approx.Length} points", area));
                continue;
            }

            if (!Cv2.IsContourConvex(approx))
            {
                rejected.Add(new RejectedContour(ToPointFs(approx), ContourRejectReason.NonConvex, "non-convex quad", area));
                continue;
            }

            var quad = OrderCorners(approx);

            if (quad.AreaPx < minArea)
            {
                rejected.Add(new RejectedContour(
                    ToPointFs(approx),
                    ContourRejectReason.MinArea,
                    $"area {quad.AreaPx:F0}px^2 < minimum {minArea}px^2",
                    quad.AreaPx));
                continue;
            }

            var aspect = quad.AspectRatio;
            if (aspect < minAspect || aspect > maxAspect)
            {
                rejected.Add(new RejectedContour(
                    ToPointFs(approx),
                    ContourRejectReason.AspectRatio,
                    $"aspect {aspect:F3} outside [{minAspect:F3}, {maxAspect:F3}]",
                    quad.AreaPx));
                continue;
            }

            if (TouchesBorder(quad, frameWidth, frameHeight, _options.BorderMarginPx))
            {
                rejected.Add(new RejectedContour(
                    ToPointFs(approx), ContourRejectReason.TouchesBorder, "corner within border margin", quad.AreaPx));
                continue;
            }

            candidates.Add(quad);
        }

        // Largest first, so the dedupe pass always compares a smaller
        // candidate against the larger quads that have already survived --
        // never the other way around -- and so the final top-N slice is a
        // plain prefix take.
        candidates.Sort((a, b) => b.AreaPx.CompareTo(a.AreaPx));

        var finalAccepted = DedupeAndTakeTopN(candidates, maxCards, rejected);

        return new DetectionDiagnostics(finalAccepted, rejected, UsedWidenedAspectTolerance: false);
    }

    private void LogRejections(IReadOnlyList<RejectedContour> rejected)
    {
        if (!_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            return;
        }

        foreach (var r in rejected)
        {
            _logger.LogDebug("Contour rejected: {Reason} ({Detail}), area {Area:F0}px^2.", r.Reason, r.Detail, r.AreaPx);
        }
    }

    /// See the type's own doc comment for the full derivation. `approx`
    /// must already be a convex 4-point polygon.
    internal static CardQuad OrderCorners(Point[] approx)
    {
        var pts = new PointF2[4];
        for (var i = 0; i < 4; i++)
        {
            pts[i] = new PointF2(approx[i].X, approx[i].Y);
        }

        var cx = (pts[0].X + pts[1].X + pts[2].X + pts[3].X) / 4f;
        var cy = (pts[0].Y + pts[1].Y + pts[2].Y + pts[3].Y) / 4f;

        var cyclic = pts
            .OrderBy(p => MathF.Atan2(p.Y - cy, p.X - cx))
            .ToArray();

        var e0 = Distance(cyclic[0], cyclic[1]);
        var e1 = Distance(cyclic[1], cyclic[2]);
        var e2 = Distance(cyclic[2], cyclic[3]);
        var e3 = Distance(cyclic[3], cyclic[0]);

        var pairA = (e0 + e2) / 2f; // candidate "top" edge (0-1) and its opposite (2-3)
        var pairB = (e1 + e3) / 2f; // the other opposite pair

        var rotated = pairA <= pairB
            ? cyclic
            : [cyclic[1], cyclic[2], cyclic[3], cyclic[0]];

        return new CardQuad(TL: rotated[0], TR: rotated[1], BR: rotated[2], BL: rotated[3]);
    }

    /// Drops nested/duplicate candidates (see `IsNestedOrDuplicate`) and
    /// caps the survivors at `maxCards`, logging a `RejectedContour` for
    /// each drop either way. `candidatesDescendingByArea` MUST already be
    /// sorted largest-first -- the caller (`RunPipeline`) guarantees this,
    /// so the dedupe pass always compares a smaller candidate against
    /// larger quads that already survived, and top-N is a plain prefix
    /// take. Pulled out of `RunPipeline` as its own `internal` method
    /// specifically so it can be unit-tested directly against hand-built
    /// overlapping quads: `RETR_EXTERNAL` (CLAUDE.md's own pinned
    /// retrieval mode) structurally excludes a genuinely nested contour
    /// from ever reaching `findContours`'s own output in the first place
    /// -- confirmed empirically here (three separate synthetic concentric-
    /// quad constructions, plus all 12 real `test-images/ad-hoc/` captures,
    /// produced zero `NestedDuplicate` rejections) -- so an end-to-end
    /// image that exercises this specific method through Canny alone is
    /// not constructible without fighting that guarantee. This method is
    /// still real, load-bearing production code: defence-in-depth against
    /// a future retrieval-mode change, or two genuinely separate detected
    /// quads that happen to overlap on an imperfect real capture.
    internal static IReadOnlyList<CardQuad> DedupeAndTakeTopN(
        List<CardQuad> candidatesDescendingByArea, int maxCards, List<RejectedContour> rejectedSink)
    {
        var accepted = new List<CardQuad>();
        foreach (var quad in candidatesDescendingByArea)
        {
            var isNestedDuplicate = accepted.Any(a => IsNestedOrDuplicate(quad, a));
            if (isNestedDuplicate)
            {
                rejectedSink.Add(new RejectedContour(
                    ToPointFs(quad),
                    ContourRejectReason.NestedDuplicate,
                    "centroid falls inside an already-accepted, larger quad",
                    quad.AreaPx));
                continue;
            }

            accepted.Add(quad);
        }

        if (accepted.Count <= maxCards)
        {
            return accepted;
        }

        foreach (var overflow in accepted.Skip(maxCards))
        {
            rejectedSink.Add(new RejectedContour(
                ToPointFs(overflow),
                ContourRejectReason.OverMaxCards,
                $"ranked below the top {maxCards} by area",
                overflow.AreaPx));
        }

        return accepted.Take(maxCards).ToList();
    }

    /// A candidate is a nested duplicate of an already-accepted quad when
    /// its centroid falls inside that quad's polygon -- true for a card's
    /// own inner frame or art-box border re-detected as a second contour,
    /// never true for two genuinely separate cards on a mat (they don't
    /// overlap in a freehand layout).
    internal static bool IsNestedOrDuplicate(CardQuad candidate, CardQuad accepted)
    {
        var poly = new[]
        {
            new Point2f(accepted.TL.X, accepted.TL.Y),
            new Point2f(accepted.TR.X, accepted.TR.Y),
            new Point2f(accepted.BR.X, accepted.BR.Y),
            new Point2f(accepted.BL.X, accepted.BL.Y),
        };

        var centroidX = (candidate.TL.X + candidate.TR.X + candidate.BR.X + candidate.BL.X) / 4f;
        var centroidY = (candidate.TL.Y + candidate.TR.Y + candidate.BR.Y + candidate.BL.Y) / 4f;

        return Cv2.PointPolygonTest(poly, new Point2f(centroidX, centroidY), false) >= 0;
    }

    private static bool TouchesBorder(CardQuad quad, int frameWidth, int frameHeight, int marginPx)
    {
        foreach (var p in new[] { quad.TL, quad.TR, quad.BR, quad.BL })
        {
            if (p.X <= marginPx || p.Y <= marginPx
                || p.X >= frameWidth - 1 - marginPx || p.Y >= frameHeight - 1 - marginPx)
            {
                return true;
            }
        }

        return false;
    }

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static IReadOnlyList<PointF2> ToPointFs(Point[] points) =>
        points.Select(p => new PointF2(p.X, p.Y)).ToList();

    private static IReadOnlyList<PointF2> ToPointFs(CardQuad quad) => [quad.TL, quad.TR, quad.BR, quad.BL];
}
