using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.Accuracy;

/// One slot's classification. `Unresolved` and `DroppedFrame` are BOTH
/// "no-match" for the purposes of the required correct@1/wrong@1/no-match
/// three-bucket 100% sum (orchestration-plan.md B6: "the three buckets sum
/// to 100%") -- from the collection's point of view a slot the harness
/// never got a confident identification for is indistinguishable from one
/// that got no confident match, either way nothing is written to the CSV.
/// They are kept as two DISTINCT enum values (rather than one "NoMatch")
/// so `AccuracyStatistics` can still report the split: `Unresolved` means
/// identification ran and produced no confident rank-1 (or no candidates at
/// all); `DroppedFrame` means the harness never attempted identification
/// for this slot because the frame's detected card count did not match its
/// ground-truth layout, and pairing by position is unsafe (see
/// `SlotMapper`'s own doc comment -- this is the H3 note block's required
/// "account for it in its own category").
public enum SlotOutcome
{
    /// Rank-1's `OracleId` matched the ground truth, regardless of distance.
    Correct,

    /// Rank-1's `OracleId` did NOT match the ground truth, AND its distance
    /// was <= `AccuracyHarnessOptions.OkDistance` -- a CONFIDENT wrong
    /// answer, the failure mode CLAUDE.md/TESTING.md call out as worse than
    /// a miss.
    Wrong,

    /// Identification ran (the frame's detected count matched its layout,
    /// so this slot WAS mapped and queried) but produced either no
    /// candidates, or a rank-1 that was wrong at a distance beyond
    /// `OkDistance` -- an honest "don't know", not a confident mistake.
    Unresolved,

    /// The frame's detected card count did not match its ground-truth
    /// layout, so no slot in this frame was mapped or queried at all --
    /// `SlotMapper.TryMapToSlots` refused rather than guess an assignment.
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
