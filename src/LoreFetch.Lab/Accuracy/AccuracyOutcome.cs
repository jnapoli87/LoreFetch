using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.Accuracy;

/// One slot's classification. `Unresolved`, `NoDetection` and
/// `DroppedFrame` are ALL "no-match" for the purposes of the required
/// correct@1/wrong@1/no-match three-bucket 100% sum (docs/history/orchestration-plan.md
/// B6: "the three buckets sum to 100%") -- from the collection's point of
/// view a slot the harness never got a confident identification for is
/// indistinguishable from one that got no confident match, either way
/// nothing is written to the CSV. They are kept as three DISTINCT enum
/// values (rather than one "NoMatch") so `AccuracyStatistics` can still
/// report the split: `Unresolved` means identification ran and produced no
/// confident rank-1 (or no candidates at all); `NoDetection` means grid
/// inference (`SlotMapper.TryInferGrid`) placed this slot's CELL with no
/// detected quad at all -- `Identify` was never called for it, because
/// there was nothing to rectify -- a detection failure, not an
/// identification one; `DroppedFrame` means grid inference could not
/// confidently place ANY of the frame's detections at all (e.g. zero
/// detections, or an ambiguous/colliding layout), so the whole frame falls
/// back to the pre-grid-inference behaviour of refusing to guess (see
/// `SlotMapper.TryInferGrid`'s own doc comment).
///
/// Keeping `NoDetection` distinct from `Unresolved` is deliberate rather
/// than cosmetic: folding a missing-card cell into `Unresolved` would hide
/// a detection failure inside the identification numbers (reviewer item 4
/// for this stream), understating how often the DETECTOR is the reason a
/// slot has no answer versus how often the IDENTIFIER is.
public enum SlotOutcome
{
    /// Rank-1's `OracleId` matched the ground truth, regardless of distance.
    Correct,

    /// Rank-1's `OracleId` did NOT match the ground truth, AND its distance
    /// was <= `AccuracyHarnessOptions.OkDistance` -- a CONFIDENT wrong
    /// answer, the failure mode DECISIONS.md/TESTING.md call out as worse than
    /// a miss.
    Wrong,

    /// Identification ran (the slot's cell WAS mapped to a detected quad
    /// and queried) but produced either no candidates, or a rank-1 that was
    /// wrong at a distance beyond `OkDistance` -- an honest "don't know",
    /// not a confident mistake.
    Unresolved,

    /// Grid inference placed this slot at a specific cell, but no detected
    /// quad landed in that cell -- the card is simply not there as far as
    /// the detector is concerned. `Identify` was never called for this
    /// slot. Distinct from `Unresolved` (which means identification itself
    /// produced no confident answer) so a detection gap is never counted as
    /// an identification failure.
    NoDetection,

    /// Grid inference (`SlotMapper.TryInferGrid`) could not confidently
    /// place the frame's detections into cells at all -- e.g. zero
    /// detections, more detections than the layout allows, or an ambiguous
    /// row/column structure it refused to guess at -- so no slot in this
    /// frame was mapped or queried at all.
    DroppedFrame,
}

/// One slot's full outcome, carrying enough to build every statistic B6
/// asks for (the bucket, the margin distribution, the per-height/per-rung
/// breakdown, the land exclusion) without re-deriving anything from raw
/// candidates later.
public sealed record SlotAccuracyResult(
    ResolvedGroundTruthRow Slot,
    SlotOutcome Outcome,
    int? Rank1Distance,
    string? Rank1OracleId,
    string? Rank1OracleName,
    int? Margin,
    int? DetectedCountInFrame);
