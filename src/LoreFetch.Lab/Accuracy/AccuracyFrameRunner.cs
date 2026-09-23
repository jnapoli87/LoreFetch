using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;

namespace LoreFetch.Lab.Accuracy;

/// Runs ONE captured frame through the exact calls the shipping scan
/// pipeline makes -- `ICardDetector.Detect` -&gt;
/// `DualHypothesisIdentification.Identify` (which itself calls
/// `IRectifier.Rectify` -&gt; `ICardIdentifier.Identify` for both the
/// as-detected and border-expanded hypotheses, package DH) -- and
/// classifies every one of the frame's ground-truth slots. Takes the
/// interfaces, not concrete types, so the same runner drives both the real
/// path (`ContourCardDetector` / `PerspectiveRectifier` /
/// `HashCardIdentifier`, against a real `CameraFrame` loaded from a fixture
/// PNG) and every synthetic/stub test in this package (`StubCardDetector` /
/// `StubRectifier` / `StubCardIdentifier`, against an in-memory
/// `CameraFrame`) -- there is no second, test-only classification path to
/// drift from the real one, and no second dual-hypothesis decision to drift
/// from `ScanPipeline`'s own.
public static class AccuracyFrameRunner
{
    /// `maxCards` passed to `Detect` is exactly `frame.Layout` -- the
    /// harness asks the detector for precisely as many cards as the ground
    /// truth says are present, matching how the app itself would drive
    /// auto-capture's "expected count" (CLAUDE.md "Interaction"). One
    /// consequence, worth stating explicitly: `ICardDetector.Detect`'s own
    /// contract is "at most maxCards", so `detected.Count` can NEVER
    /// exceed `frame.Layout` through this call -- an under-count reachable
    /// from real usage is always under-detection (fewer found than
    /// expected), never over-detection above it, matching B5a's own
    /// real-capture evidence (a sleeved card went undetected; nothing
    /// false-positived an extra card into an already-full count).
    /// `SlotMapper.TryInferGrid` still refuses an over-count defensively as
    /// a property of its own general contract (see its own tests), even
    /// though this call site can never produce one.
    ///
    /// Slot assignment is GRID-INFERRED (`SlotMapper.TryInferGrid`), not a
    /// strict "detected count == layout" requirement -- an under-detected
    /// 3x3 frame (measured on the real corpus: most frames find 8 of 9,
    /// only two find all 9 -- see docs/accuracy.md) still classifies its
    /// other cells instead of the whole frame being dropped. A cell with
    /// no detected quad becomes `SlotOutcome.NoDetection` for that slot
    /// specifically -- `Identify` is never called for it, since there is
    /// nothing to rectify. Only when the grid itself cannot be confidently
    /// inferred at all (zero detections, an ambiguous/colliding layout --
    /// see `TryInferGrid`'s own doc comment) does the ENTIRE frame fall
    /// back to `DroppedFrame`, the pre-grid-inference behaviour.
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
        var (rows, cols) = SlotMapper.GridDimensionsForLayout(frame.Layout);

        if (!SlotMapper.TryInferGrid(detected, rows, cols, out var cellsBySlot))
        {
            // The grid itself could not be confidently inferred (0
            // detections, more detections than cells, or an ambiguous/
            // colliding row-or-column structure) -- every slot in this
            // frame is accounted for as DroppedFrame -- none are silently
            // skipped, and none are misassigned to a detected quad that
            // does not correspond to them.
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
            var quad = cellsBySlot![i];

            if (quad is null)
            {
                // The grid placed this slot at a specific cell, but no
                // detected quad landed there -- a detection gap, not an
                // identification one. `Identify` is never called.
                results.Add(new SlotAccuracyResult(
                    slot, SlotOutcome.NoDetection, Rank1Distance: null, Rank1OracleId: null,
                    Rank1OracleName: null, Margin: null, DetectedCountInFrame: detected.Count));
                continue;
            }

            var outcome = DualHypothesisIdentification.Identify(cameraFrame, quad.Value, rectifier, identifier, options.MaxCandidates);

            results.Add(Classify(slot, outcome.Candidates, options.OkDistance, detected.Count));
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
