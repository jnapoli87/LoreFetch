using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;

namespace LoreFetch.Core.Scanning;

/// Which hypothesis a `DualHypothesisIdentification.Identify` call returned
/// candidates from.
public enum IdentificationHypothesis
{
    /// The detector's own quad, unmodified.
    AsDetected,

    /// The detector's quad, grown by `QuadExpansion`'s committed border-
    /// correction factors.
    Expanded,
}

/// Package DH: dual-hypothesis identification. `docs/history/orchestration-plan.md`'s
/// 2026-09-22 "dual-hypothesis identification ships in v0.1.0" ruling is the
/// evidence this exists to act on -- E1a found that on a tight grid
/// (`tight_white`) and on a black mat, adjacent black borders merge or a
/// black border loses contrast against a black mat, so `ContourCardDetector`
/// accepts a quad sitting on the border's INNER edge rather than the card's
/// true outer edge. The rectified crop is clean but borderless, while every
/// reference render carries its border -- every card on those frames
/// identified at a noise-level distance (~290-350).
///
/// Measured fix (`lab expand-experiment`, re-run by the orchestrator):
/// identifying BOTH the as-detected quad and the same quad grown by
/// `QuadExpansion.BorderWidthCorrectionFactor`/`BorderHeightCorrectionFactor`
/// (before rectification, so the correction widens the SOURCE geometry
/// rather than resampling an already-canonical 488x680 card), then keeping
/// whichever hypothesis's own candidate list has the LOWER top-1 distance,
/// took non-land correct@1 on the 15in integration corpus from 22/38
/// (baseline only) to 37/38 (dual) -- with 0 regressions (a baseline-correct
/// card never became dual-wrong) and 0 wrong matches at distance &lt;= 240
/// under either hypothesis alone. Expanded-only (17/38) is a net
/// regression on its own, so BOTH hypotheses are needed together.
///
/// This is the ONE place that decision is made -- `ScanPipeline` and the
/// Lab accuracy harness (`AccuracyFrameRunner`) both call this instead of
/// calling `IRectifier.Rectify`/`ICardIdentifier.Identify` directly, so the
/// accuracy numbers measured by the harness are exactly what the app does.
///
/// **No thresholds anywhere in this type.** The decision is a pure
/// min-distance comparison between the two hypotheses' own top-1 candidate
/// distances (ties favour AsDetected, since it is the geometry that needed
/// no correction) -- never a comparison against `goodDistance`/`okDistance`
/// or any other hardcoded value. Those are applied downstream, by
/// `ScanPipeline`'s own `CohortTile` construction, exactly as before.
///
/// **No RectifiedCard lifetime concerns**: `RectifiedCard` is "plain
/// managed memory, deliberately NOT pooled and NOT IDisposable"
/// (`Core/Abstractions/Detection.cs`'s own doc comment) -- rectifying twice
/// per tile (once per hypothesis) allocates two ordinary managed buffers
/// that the GC reclaims once this method returns and the losing
/// hypothesis's `RectifiedCard` goes out of scope; there is no pool to
/// leak from and no disposal contract to honour. The `CameraFrame` this
/// method reads from is neither owned nor disposed here -- exactly as
/// today's single-hypothesis call site never disposed it either; the
/// caller (`ScanPipeline.TryCaptureFromRetained`) still owns that lifetime
/// in its own `finally` block, unchanged.
public static class DualHypothesisIdentification
{
    /// `Candidates` is exactly one hypothesis's own `ICardIdentifier.Identify`
    /// output, verbatim -- never a per-oracle merge of both -- so it keeps
    /// every guarantee `ICardIdentifier.Identify` already makes (ranked,
    /// distinct-oracle, unfiltered by threshold). `Winner` and
    /// `ExpandedHypothesisSkipped` are reported so a caller can log which
    /// hypothesis won per tile, per the package brief.
    public readonly record struct Result(
        IReadOnlyList<CardCandidate> Candidates,
        IdentificationHypothesis Winner,
        bool ExpandedHypothesisSkipped);

    /// Same as <see cref="Result"/>, plus the winning hypothesis's own
    /// rectified image -- what <see cref="Identify"/> returns, since its
    /// caller (`ScanPipeline`) needs an image for `CohortTile.Image`, not
    /// just candidates. The image shown/hashed is always the one that
    /// actually produced `Candidates`, so the thumbnail a user sees never
    /// disagrees with what was matched.
    public readonly record struct IdentificationOutcome(
        RectifiedCard Image,
        IReadOnlyList<CardCandidate> Candidates,
        IdentificationHypothesis Winner,
        bool ExpandedHypothesisSkipped);

    /// Rectifies and identifies `detectedQuad` (the "as-detected" hypothesis),
    /// then does the same for `detectedQuad` grown by `QuadExpansion`'s
    /// committed correction factors (the "expanded" hypothesis) -- unless
    /// the expanded quad would sample outside `frame`'s own bounds, in which
    /// case that hypothesis is SKIPPED (never clamped: clamping would
    /// silently distort the border-correction geometry for a card sitting
    /// near a frame edge, trading one silent failure for another). Returns
    /// whichever hypothesis's own candidate list has the lower top-1
    /// distance, together with that hypothesis's own rectified image; ties
    /// -- including "both hypotheses returned zero candidates" -- favour
    /// AsDetected.
    public static IdentificationOutcome Identify(
        CameraFrame frame,
        CardQuad detectedQuad,
        IRectifier rectifier,
        ICardIdentifier identifier,
        int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(rectifier);
        ArgumentNullException.ThrowIfNull(identifier);

        var asDetectedCard = rectifier.Rectify(frame, detectedQuad);
        var asDetectedCandidates = identifier.Identify(asDetectedCard, maxCandidates);

        if (!TryExpand(detectedQuad, frame.Width, frame.Height, out var expandedQuad))
        {
            return new IdentificationOutcome(
                asDetectedCard, asDetectedCandidates, IdentificationHypothesis.AsDetected, ExpandedHypothesisSkipped: true);
        }

        var expandedCard = rectifier.Rectify(frame, expandedQuad);
        var expandedCandidates = identifier.Identify(expandedCard, maxCandidates);

        var selection = SelectWinner(asDetectedCandidates, expandedCandidates);
        var winningImage = selection.Winner == IdentificationHypothesis.Expanded ? expandedCard : asDetectedCard;

        return new IdentificationOutcome(winningImage, selection.Candidates, selection.Winner, selection.ExpandedHypothesisSkipped);
    }

    /// Grows `quad` by the committed border-correction factors about its
    /// own centroid (see `QuadExpansion.Expand`). Returns `false` -- rather
    /// than a clamped, in-bounds quad -- when any of the four expanded
    /// corners would fall outside `[0, frameWidth) x [0, frameHeight)`, so
    /// a card near the frame's edge never has its expanded hypothesis
    /// silently distorted by clamping; the caller simply has one fewer
    /// hypothesis to compare that tile.
    public static bool TryExpand(CardQuad quad, int frameWidth, int frameHeight, out CardQuad expandedQuad)
    {
        expandedQuad = QuadExpansion.Expand(
            quad, QuadExpansion.BorderWidthCorrectionFactor, QuadExpansion.BorderHeightCorrectionFactor);

        return InBounds(expandedQuad.TL, frameWidth, frameHeight)
            && InBounds(expandedQuad.TR, frameWidth, frameHeight)
            && InBounds(expandedQuad.BR, frameWidth, frameHeight)
            && InBounds(expandedQuad.BL, frameWidth, frameHeight);
    }

    /// The pure decision, factored out so a caller that has already
    /// computed both candidate lists some other way (e.g. `LoreFetch.Lab`'s
    /// diagnostic tooling, which explores factors other than the committed
    /// ones) can still make EXACTLY this decision rather than a
    /// re-implementation of it. `expandedCandidates` is `null` when the
    /// expanded hypothesis was never computed (skipped) -- distinct from an
    /// empty list, which means the expanded hypothesis WAS computed but the
    /// identifier returned nothing.
    public static Result SelectWinner(
        IReadOnlyList<CardCandidate> asDetectedCandidates, IReadOnlyList<CardCandidate>? expandedCandidates)
    {
        ArgumentNullException.ThrowIfNull(asDetectedCandidates);

        if (expandedCandidates is null)
        {
            return new Result(asDetectedCandidates, IdentificationHypothesis.AsDetected, ExpandedHypothesisSkipped: true);
        }

        var asDetectedTop1 = Top1Distance(asDetectedCandidates);
        var expandedTop1 = Top1Distance(expandedCandidates);

        // Strictly less-than: a tie (including "neither hypothesis
        // returned any candidate") favours AsDetected, per the package
        // brief -- it is the geometry that needed no correction, so it is
        // the more conservative default when the two hypotheses disagree
        // by nothing.
        return expandedTop1 < asDetectedTop1
            ? new Result(expandedCandidates, IdentificationHypothesis.Expanded, ExpandedHypothesisSkipped: false)
            : new Result(asDetectedCandidates, IdentificationHypothesis.AsDetected, ExpandedHypothesisSkipped: false);
    }

    private static int Top1Distance(IReadOnlyList<CardCandidate> candidates) =>
        candidates.Count > 0 ? candidates[0].Distance : int.MaxValue;

    private static bool InBounds(PointF2 p, int width, int height) =>
        p.X >= 0f && p.X <= width - 1f && p.Y >= 0f && p.Y <= height - 1f;
}
