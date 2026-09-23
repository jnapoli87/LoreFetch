using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.StreamB.Accuracy;

/// Classification logic (Correct/Wrong/Unresolved/DroppedFrame), tested
/// against the FAKES (`StubCardDetector`/`StubRectifier` from
/// `Core/Fakes`, plus a small local `ScriptedIdentifier`) rather than real
/// images -- `AccuracyFrameRunner` takes interfaces, so this exercises the
/// exact same classification code the real path uses, without needing
/// OpenCV or a real index. The real-detector, real-image version of the
/// same run lives in `AccuracyHarnessSyntheticTests`.
public class AccuracyFrameRunnerTests
{
    /// Package DH: `AccuracyFrameRunner` now identifies through
    /// `DualHypothesisIdentification.Identify`, which calls
    /// `ICardIdentifier.Identify` TWICE per slot (as-detected, then
    /// expanded) whenever the expanded quad stays inside the frame --
    /// `StubCardDetector`'s own margin/gutter geometry always leaves that
    /// much room, so every `ScriptedIdentifier` program below needs one
    /// deliberately-worse second response per slot (via
    /// `WorseSecondHypothesis`) so the AS-DETECTED hypothesis keeps
    /// winning and every existing assertion's outcome is unchanged --
    /// these tests are about `AccuracyFrameRunner`'s own classification
    /// logic, not about which dual-hypothesis winner is picked (that
    /// decision has its own coverage: `DualHypothesisIdentificationTests`,
    /// Tests/Integration).
    [Fact]
    public void Run_RankOneMatchesExpected_ClassifiesCorrect()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([
            [Candidate("oracle-solring", "Sol Ring", 40), Candidate("other", "Other", 300)],
            WorseSecondHypothesis(40),
        ]);

        var results = Run(frame, detectedCount: 1, identifier);

        Assert.Equal(SlotOutcome.Correct, results[0].Outcome);
        Assert.Equal(40, results[0].Rank1Distance);
        Assert.Equal(260, results[0].Margin);
    }

    [Fact]
    public void Run_RankOneWrongButWithinOkDistance_ClassifiesWrong()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([
            [Candidate("oracle-mountain", "Mountain", 200), Candidate("oracle-solring", "Sol Ring", 260)],
            WorseSecondHypothesis(200),
        ]);

        var results = Run(frame, detectedCount: 1, identifier, options: new AccuracyHarnessOptions { OkDistance = 270 });

        Assert.Equal(SlotOutcome.Wrong, results[0].Outcome);
        Assert.Equal("oracle-mountain", results[0].Rank1OracleId);
    }

    [Fact]
    public void Run_RankOneWrongAndBeyondOkDistance_ClassifiesUnresolved()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([
            [Candidate("oracle-mountain", "Mountain", 350), Candidate("oracle-solring", "Sol Ring", 360)],
            WorseSecondHypothesis(350),
        ]);

        var results = Run(frame, detectedCount: 1, identifier, options: new AccuracyHarnessOptions { OkDistance = 270 });

        Assert.Equal(SlotOutcome.Unresolved, results[0].Outcome);
    }

    [Fact]
    public void Run_NoCandidatesReturned_ClassifiesUnresolved()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([[], []]);

        var results = Run(frame, detectedCount: 1, identifier);

        Assert.Equal(SlotOutcome.Unresolved, results[0].Outcome);
        Assert.Null(results[0].Rank1Distance);
        Assert.Null(results[0].Margin);
    }

    /// Requirement B end to end through `AccuracyFrameRunner` itself (not
    /// just `SlotMapper` in isolation): a 3-slot ground-truth frame where
    /// the detector finds only 2 cards must mark EVERY slot DroppedFrame,
    /// and `Identify` must never even be called (the scripted identifier
    /// has zero programmed responses -- calling it at all throws).
    [Fact]
    public void Run_DetectedCountBelowLayout_MarksEveryFrameSlotDroppedAndNeverCallsIdentify()
    {
        var frame = BuildFrame(
            layout: 3,
            ("Card A", "oracle-a", false), ("Card B", "oracle-b", false), ("Card C", "oracle-c", false));
        var identifier = new ScriptedIdentifier([]); // no responses programmed -- a call would throw

        var results = Run(frame, detectedCount: 2, identifier); // 2 detected, 3 expected

        Assert.All(results, r => Assert.Equal(SlotOutcome.DroppedFrame, r.Outcome));
        Assert.All(results, r => Assert.Equal(2, r.DetectedCountInFrame));
        Assert.All(results, r => Assert.Null(r.Rank1Distance));
    }

    /// Task 2 (orchestration-plan.md): the headline case grid inference
    /// exists for. A 3x3 (layout 9) frame where the detector finds only 8
    /// of 9 -- exactly the real corpus's own most common outcome (see
    /// docs/accuracy.md) -- must classify the other 8 slots normally
    /// (`Identify` called exactly 8 times, never 9 -- the `ScriptedIdentifier`
    /// is programmed with exactly 8 responses, so a 9th call would throw)
    /// and mark the missing slot `NoDetection`, NOT `DroppedFrame`.
    ///
    /// Also the concrete form of the brief's "buckets sum to 100%" chaos
    /// requirement: `results.Count` must stay 9 (one result per
    /// ground-truth slot, including the missing one) -- a version of
    /// `AccuracyFrameRunner` that silently `continue`d past a null cell
    /// instead of emitting a `NoDetection` result would produce only 8
    /// results here, which `AccuracyBucketCounts.AssertBucketsSumToTotal`
    /// downstream would then catch as a mismatch against the frame's own
    /// 9 ground-truth slots (asserted directly below, and chaos-tested by
    /// reverting the fix -- see this package's own commit).
    [Fact]
    public void Run_NineExpectedButOnlyEightDetected_ClassifiesEightSlotsAndOneNoDetection()
    {
        var frame = BuildFrame(
            layout: 9,
            ("Card 1", "oracle-1", false), ("Card 2", "oracle-2", false), ("Card 3", "oracle-3", false),
            ("Card 4", "oracle-4", false), ("Card 5", "oracle-5", false), ("Card 6", "oracle-6", false),
            ("Card 7", "oracle-7", false), ("Card 8", "oracle-8", false), ("Card 9", "oracle-9", false));

        // StubCardDetector(8)'s own row-major placement (GenericGrid(8) ==
        // 3x3, filled sequentially) leaves exactly the LAST cell (slot 9)
        // empty -- see StubCardDetector.BuildLayout/GenericGrid. Two
        // responses per detected slot (as-detected, then a deliberately
        // worse expanded hypothesis) -- see this class's own doc comment.
        var responses = new List<IReadOnlyList<CardCandidate>>();
        for (var i = 1; i <= 8; i++)
        {
            responses.Add([Candidate($"oracle-{i}", $"Card {i}", 40)]);
            responses.Add(WorseSecondHypothesis(40));
        }

        var identifier = new ScriptedIdentifier(responses); // exactly 16 (8 slots x 2 hypotheses) -- a 17th call throws

        var results = Run(frame, detectedCount: 8, identifier);

        Assert.Equal(9, results.Count); // one per ground-truth slot -- none silently dropped
        Assert.Equal(SlotOutcome.NoDetection, results[8].Outcome); // slot 9
        Assert.Null(results[8].Rank1Distance);
        Assert.Null(results[8].Rank1OracleId);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(SlotOutcome.Correct, results[i].Outcome);
            Assert.Equal($"oracle-{i + 1}", results[i].Rank1OracleId);
        }

        var buckets = AccuracyBucketCounts.From(results);
        buckets.AssertBucketsSumToTotal(results.Count); // the 100% sum, asserted in code
        Assert.Equal(1, buckets.NoDetection);
        Assert.Equal(8, buckets.Correct);
    }

    /// NOTE: there is deliberately no "detected count ABOVE layout" case
    /// here alongside the "below" case above -- see
    /// `AccuracyFrameRunner.Run`'s own doc comment on `maxCards`. Because
    /// the runner asks the detector for at most `frame.Layout` quads (the
    /// same "expected count" the real app would configure), and
    /// `ICardDetector.Detect`'s own contract is "at most maxCards", the
    /// detected count can structurally never exceed the layout through
    /// this call convention -- `StubCardDetector(5).Detect(frame,
    /// maxCards: 3)` returns exactly 3, not 5. Over-detection above the
    /// expected count is therefore unreachable here; `SlotMapper`'s own
    /// unit tests (`TryMapToSlots_MoreDetectedThanExpected_...`) still
    /// cover it defensively as a property of `SlotMapper` in isolation.
    [Fact]
    public void Run_ZeroDetections_MarksEveryFrameSlotDropped()
    {
        var frame = BuildFrame(layout: 3, ("Card A", "oracle-a", false), ("Card B", "oracle-b", false), ("Card C", "oracle-c", false));
        var identifier = new ScriptedIdentifier([]);

        var results = Run(frame, detectedCount: 0, identifier);

        Assert.All(results, r => Assert.Equal(SlotOutcome.DroppedFrame, r.Outcome));
    }

    private static IReadOnlyList<SlotAccuracyResult> Run(
        GroundTruthFrame frame, int detectedCount, ICardIdentifier identifier, AccuracyHarnessOptions? options = null)
    {
        var detector = new StubCardDetector(detectedCount);
        var rectifier = new StubRectifier();
        var cameraFrame = new CameraFrame(new byte[1200 * 900 * 3], 1200, 900, 1200 * 3, PixelLayout.Bgr24, DateTimeOffset.UtcNow, pool: null);
        using (cameraFrame)
        {
            return AccuracyFrameRunner.Run(frame, cameraFrame, detector, rectifier, identifier, options ?? AccuracyHarnessOptions.Default);
        }
    }

    private static CardCandidate Candidate(string oracleId, string oracleName, int distance) =>
        new(oracleId, oracleName, distance, ArtworkId: $"art-{oracleId}");

    /// A single-candidate response for the "expanded" hypothesis slot in a
    /// `ScriptedIdentifier` program, deliberately at a distance strictly
    /// worse (higher) than `asDetectedTop1Distance` -- so
    /// `DualHypothesisIdentification.SelectWinner`'s strict less-than
    /// comparison always keeps the AS-DETECTED hypothesis, leaving this
    /// file's existing outcome assertions unaffected by the extra call.
    private static IReadOnlyList<CardCandidate> WorseSecondHypothesis(int asDetectedTop1Distance) =>
        [Candidate("oracle-worse-expanded-hypothesis", "Worse Expanded Hypothesis", asDetectedTop1Distance + 500)];

    private static GroundTruthFrame BuildFrame(int layout, params (string OracleName, string OracleId, bool IsBasicLand)[] cards)
    {
        Assert.Equal(layout, cards.Length);
        var slots = new List<ResolvedGroundTruthRow>();
        for (var i = 0; i < cards.Length; i++)
        {
            var (oracleName, oracleId, isBasicLand) = cards[i];
            var row = new GroundTruthRow("f.png", 15, layout, i + 1, oracleName, "normal", "light");
            slots.Add(new ResolvedGroundTruthRow(row, oracleId, isBasicLand));
        }

        return new GroundTruthFrame("f.png", 15, layout, slots);
    }

    /// A minimal, fully controllable `ICardIdentifier` test double: one
    /// programmed candidate list per call, in order. Throws if called more
    /// times than programmed -- which is exactly what makes
    /// `Run_DetectedCountBelowLayout_...` prove `Identify` is never called
    /// on a dropped frame (an empty program + any call = a thrown
    /// exception, not a silently-wrong classification).
    private sealed class ScriptedIdentifier : ICardIdentifier
    {
        private readonly Queue<IReadOnlyList<CardCandidate>> _responses;

        public ScriptedIdentifier(IEnumerable<IReadOnlyList<CardCandidate>> responses)
        {
            _responses = new Queue<IReadOnlyList<CardCandidate>>(responses);
        }

        public string Name => "Scripted";

        public IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedIdentifier.Identify called with no programmed response left.");
            }

            return _responses.Dequeue();
        }
    }
}
