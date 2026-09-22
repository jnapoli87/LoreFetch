using LoreFetch.Core.Abstractions;

namespace LoreFetch.Lab.Accuracy;

/// Runs ONE captured frame through the exact three calls the shipping scan
/// pipeline makes -- `ICardDetector.Detect` -&gt; `IRectifier.Rectify` -&gt;
/// `ICardIdentifier.Identify` -- and classifies every one of the frame's
/// ground-truth slots. Takes the interfaces, not concrete types, so the
/// same runner drives both the real path (`ContourCardDetector` /
/// `PerspectiveRectifier` / `HashCardIdentifier`, against a real
/// `CameraFrame` loaded from a fixture PNG) and every synthetic/stub test
/// in this package (`StubCardDetector` / `StubRectifier` /
/// `StubCardIdentifier`, against an in-memory `CameraFrame`) -- there is no
/// second, test-only classification path to drift from the real one.
public static class AccuracyFrameRunner
{
    /// `maxCards` passed to `Detect` is exactly `frame.Layout` -- the
    /// harness asks the detector for precisely as many cards as the ground
    /// truth says are present, matching how the app itself would drive
    /// auto-capture's "expected count" (CLAUDE.md "Interaction"). One
    /// consequence, worth stating explicitly: `ICardDetector.Detect`'s own
    /// contract is "at most maxCards", so `detected.Count` can NEVER
    /// exceed `frame.Layout` through this call -- a count mismatch
    /// reachable from real usage is therefore always under-detection
    /// (fewer found than expected), never over-detection above it, which
    /// matches B5a's own real-capture evidence (a sleeved card went
    /// undetected; nothing false-positived an extra card into an
    /// already-full count). `SlotMapper.TryMapToSlots` still refuses an
    /// over-count defensively as a property of its own general contract
    /// (see its own tests), even though this call site can never produce
    /// one.
    public static IReadOnlyList<SlotAccuracyResult> Run(
        GroundTruthFrame frame,
        CameraFrame cameraFrame,
        ICardDetector detector,
        IRectifier rectifier,
        ICardIdentifier identifier,
        AccuracyHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(cameraFrame);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(rectifier);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(options);

        var detected = detector.Detect(cameraFrame, frame.Layout);

        if (!SlotMapper.TryMapToSlots(detected, frame.Layout, out var orderedBySlot))
        {
            // Requirement B: a count mismatch is never paired by position.
            // Every slot in this frame is accounted for as DroppedFrame --
            // none are silently skipped, and none are misassigned to a
            // detected quad that does not correspond to them.
            return frame.Slots
                .Select(slot => new SlotAccuracyResult(
                    slot, SlotOutcome.DroppedFrame, Rank1Distance: null, Rank1OracleId: null,
                    Rank1OracleName: null, Margin: null, DetectedCountInFrame: detected.Count))
                .ToList();
        }

        var results = new List<SlotAccuracyResult>(frame.Slots.Count);
        for (var i = 0; i < frame.Slots.Count; i++)
        {
            var slot = frame.Slots[i];
            var quad = orderedBySlot![i];

            var card = rectifier.Rectify(cameraFrame, quad);
            var candidates = identifier.Identify(card, options.MaxCandidates);

            results.Add(Classify(slot, candidates, options.OkDistance, detected.Count));
        }

        return results;
    }

    private static SlotAccuracyResult Classify(
        ResolvedGroundTruthRow slot, IReadOnlyList<CardCandidate> candidates, int okDistance, int detectedCount)
    {
        if (candidates.Count == 0)
        {
            return new SlotAccuracyResult(
                slot, SlotOutcome.Unresolved, Rank1Distance: null, Rank1OracleId: null,
                Rank1OracleName: null, Margin: null, DetectedCountInFrame: detectedCount);
        }

        var rank1 = candidates[0];
        var margin = candidates.Count > 1 ? candidates[1].Distance - rank1.Distance : (int?)null;

        var isCorrect = string.Equals(rank1.OracleId, slot.ExpectedOracleId, StringComparison.Ordinal);
        var outcome = isCorrect
            ? SlotOutcome.Correct
            : rank1.Distance <= okDistance
                ? SlotOutcome.Wrong
                : SlotOutcome.Unresolved;

        return new SlotAccuracyResult(
            slot, outcome, rank1.Distance, rank1.OracleId, rank1.OracleName, margin, detectedCount);
    }
}
