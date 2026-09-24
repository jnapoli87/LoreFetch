using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab;
using LoreFetch.Lab.CropScale;
using LoreFetch.Lab.RoundTrip;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Lab.CropScale;

/// Package B5c, part 2: "the real-frame check" -- runs the crop-scale
/// analysis against the 12 real C920 frames in `test-images/ad-hoc/`
/// (rather than clean Scryfall renders), and specifically asks whether a
/// crop-scale CORRECTION recovers the correct match on `plains_black`, the
/// known B5b failure (Mountain wins at distance 345; the correct Plains is
/// not rank 1). Artifact-gated exactly like `ContourCardDetectorRealCaptureTests`
/// / `RoundTripGateTests` (`RealCaptureGate`): a missing `test-images/ad-hoc/`
/// or missing committed index is a SKIP with a reason under the CI default,
/// never a failure -- card imagery can never be committed.
///
/// Two things this test measures, in order:
///   1. A NUMERIC inset estimate for `plains_black`, from real detector
///      geometry rather than "by eye" (B5b's own words) -- `plains_white`
///      and `plains_brown` both landed on the card's TRUE outer edge
///      (B5b's tally: both correct at rank 1), so their quad dimensions
///      are the best available ground truth for "this exact physical
///      card, this exact camera setup, no inset" -- better than comparing
///      across different cards (e.g. Sol Ring), which also varies the
///      print itself.
///   2. Whether applying `CropScaleTransform` with a NEGATIVE (outset)
///      correction to `plains_black`'s already-rectified query card, at
///      that estimated fraction and a small bracket around it, recovers
///      Plains to rank 1 or meaningfully closes the distance gap -- this
///      is the concrete evidence for or against the identifier-side
///      "3-scale sweep" fix option.
public class CropScaleRealFrameTests
{
    private const string PlainsBlack = "plains_black.png";
    private const string PlainsWhite = "plains_white.png";
    private const string PlainsBrown = "plains_brown.png";

    private readonly ITestOutputHelper _output;

    public CropScaleRealFrameTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PlainsBlack_CropScaleCorrection_MeasuredAgainstRealGroundTruth()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root to find test-images/ad-hoc/ and the committed index.");
            return;
        }

        var adHocDir = Path.Combine(repoRoot!, "test-images", "ad-hoc");
        if (!Directory.Exists(adHocDir))
        {
            RealCaptureGate.SkipOrFail($"real C920 fixtures not present at \"{adHocDir}\" -- card imagery can never be committed.");
            return;
        }

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            RealCaptureGate.SkipOrFail($"committed hash index not present at \"{indexPath}\".");
            return;
        }

        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var rectifier = new PerspectiveRectifier();
        var identifier = HashCardIdentifier.Load(indexPath, NullLoggerFactory.Instance);

        var plainsOracleId = identifier.All.Where(o => o.OracleName == "Plains").Select(o => o.OracleId).FirstOrDefault()
            ?? throw new InvalidOperationException("CropScaleRealFrameTests: \"Plains\" not found in the committed index's oracle table.");

        // Step 1: ground truth from the SAME physical card, correctly
        // detected on two other mats.
        var whiteQuad = DetectSingleQuad(detector, Path.Combine(adHocDir, PlainsWhite));
        var brownQuad = DetectSingleQuad(detector, Path.Combine(adHocDir, PlainsBrown));
        var trueWidth = (QuadWidth(whiteQuad) + QuadWidth(brownQuad)) / 2f;
        var trueHeight = (QuadHeight(whiteQuad) + QuadHeight(brownQuad)) / 2f;

        var blackQuad = DetectSingleQuad(detector, Path.Combine(adHocDir, PlainsBlack));
        var blackWidth = QuadWidth(blackQuad);
        var blackHeight = QuadHeight(blackQuad);

        var widthInset = 1f - (blackWidth / trueWidth);
        var heightInset = 1f - (blackHeight / trueHeight);

        _output.WriteLine(
            $"Ground truth (plains_white/plains_brown avg): {trueWidth:F1}x{trueHeight:F1}px. " +
            $"plains_black: {blackWidth:F1}x{blackHeight:F1}px. " +
            $"Measured inset: {widthInset:P1} width, {heightInset:P1} height.");

        // B5b's own by-eye estimate was "about 5% inset per side in width,
        // 3-4% in height" -- this asserts the geometric measurement lands
        // in the same neighbourhood (loose bounds: real capture geometry
        // carries fixture-to-fixture noise this is not trying to pin
        // exactly), so a future re-shoot that drifts far outside "roughly
        // a black-border-sized inset" is caught rather than silently
        // trusted.
        Assert.InRange(widthInset, 0.01f, 0.15f);
        Assert.InRange(heightInset, 0.005f, 0.12f);

        // Step 2: baseline (no correction) -- reproduces B5b's finding.
        using var blackFrameColor = Cv2.ImRead(Path.Combine(adHocDir, PlainsBlack), ImreadModes.Color);
        using var blackFrame = FrameMat.FromMat(blackFrameColor);
        var blackCard = rectifier.Rectify(blackFrame, blackQuad);

        var baseline = Identify(identifier, blackCard, plainsOracleId);
        _output.WriteLine($"Baseline (no correction): {DescribeOutcome(baseline)}");

        // Step 3: a small bracket of OUTSET corrections -- CropScaleTransform
        // with a NEGATIVE fraction samples a wider virtual region (border-
        // replicated) and shrinks it back down, which is the operation that
        // would "zoom back out" a query that came from an inset quad. This
        // is exactly what a `HashCardIdentifier`-internal 3-scale sweep
        // would try per query, evaluated here directly against the one
        // real frame this package's brief names.
        float[] correctionFractions = [0f, -0.03f, -0.05f, -0.08f, -widthInset];
        var results = new List<(float Fraction, CorrectionOutcome Outcome)>();
        using var blackCardMat = QueryTransform.ToMat(blackCard);

        foreach (var fraction in correctionFractions.Distinct())
        {
            using var corrected = CropScaleTransform.Apply(blackCardMat, fraction, fraction * (heightInset / widthInset));
            var correctedCard = CardImageLoader.ToRectifiedCard(corrected);
            var outcome = Identify(identifier, correctedCard, plainsOracleId);
            results.Add((fraction, outcome));
            _output.WriteLine($"Correction {fraction:F2}: {DescribeOutcome(outcome)}");
        }

        var bestByDistance = results.OrderBy(r => r.Outcome.PlainsDistance).First();
        _output.WriteLine($"Best correction by Plains distance: {bestByDistance.Fraction:F2} -> {DescribeOutcome(bestByDistance.Outcome)}");

        // This is a MEASUREMENT, not a pass/fail gate: the brief's job is
        // to report whether correction recovers the match, with numbers,
        // not to force it to succeed. The one thing asserted is that the
        // baseline reproduces a wrong rank-1 (B5b's own finding) -- if a
        // future re-shoot or index rebuild makes plains_black correct
        // WITHOUT any correction, that is good news that invalidates this
        // test's premise and must be looked at, not silently absorbed.
        Assert.False(
            baseline.Rank1OracleId == plainsOracleId,
            "plains_black now matches correctly with NO correction -- B5b's premise for this test no longer " +
            "holds; update or remove this test rather than silently keep asserting around a fixed baseline.");
    }

    private static CardQuad DetectSingleQuad(ContourCardDetector detector, string imagePath)
    {
        using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
        using var frame = FrameMat.FromMat(color);
        var detected = detector.Detect(frame, maxCards: 9);
        if (detected.Count != 1)
        {
            throw new InvalidOperationException(
                $"CropScaleRealFrameTests: expected exactly one detection in \"{imagePath}\", got {detected.Count}.");
        }

        return detected[0];
    }

    private static float QuadWidth(CardQuad quad) => (Distance(quad.TL, quad.TR) + Distance(quad.BL, quad.BR)) / 2f;

    private static float QuadHeight(CardQuad quad) => (Distance(quad.TL, quad.BL) + Distance(quad.TR, quad.BR)) / 2f;

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct CorrectionOutcome(string? Rank1OracleId, string? Rank1OracleName, int Rank1Distance, int PlainsDistance);

    private static CorrectionOutcome Identify(HashCardIdentifier identifier, RectifiedCard card, string plainsOracleId)
    {
        var candidates = identifier.Identify(card, maxCandidates: 5);
        var top = candidates.Count > 0 ? candidates[0] : (CardCandidate?)null;
        var plains = candidates.FirstOrDefault(c => c.OracleId == plainsOracleId);
        // A default CardCandidate (not found) has Distance 0 and a null
        // OracleId -- distinguish "not in top 5" from "found at distance 0"
        // by OracleId, not by the distance value alone.
        var plainsDistance = plains.OracleId == plainsOracleId ? plains.Distance : int.MaxValue;

        return new CorrectionOutcome(top?.OracleId, top?.OracleName, top?.Distance ?? -1, plainsDistance);
    }

    private static string DescribeOutcome(CorrectionOutcome outcome) =>
        $"top1={outcome.Rank1OracleName ?? "(none)"} (Distance={outcome.Rank1Distance}), " +
        $"Plains at distance {(outcome.PlainsDistance == int.MaxValue ? "not in top 5" : outcome.PlainsDistance.ToString())}";
}
