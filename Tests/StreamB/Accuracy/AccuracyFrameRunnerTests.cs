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
    [Fact]
    public void Run_RankOneMatchesExpected_ClassifiesCorrect()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([[Candidate("oracle-solring", "Sol Ring", 40), Candidate("other", "Other", 300)]]);

        var results = Run(frame, detectedCount: 1, identifier);

        Assert.Equal(SlotOutcome.Correct, results[0].Outcome);
        Assert.Equal(40, results[0].Rank1Distance);
        Assert.Equal(260, results[0].Margin);
    }

    [Fact]
    public void Run_RankOneWrongButWithinOkDistance_ClassifiesWrong()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([[Candidate("oracle-mountain", "Mountain", 200), Candidate("oracle-solring", "Sol Ring", 260)]]);

        var results = Run(frame, detectedCount: 1, identifier, options: new AccuracyHarnessOptions { OkDistance = 270 });

        Assert.Equal(SlotOutcome.Wrong, results[0].Outcome);
        Assert.Equal("oracle-mountain", results[0].Rank1OracleId);
    }

    [Fact]
    public void Run_RankOneWrongAndBeyondOkDistance_ClassifiesUnresolved()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([[Candidate("oracle-mountain", "Mountain", 350), Candidate("oracle-solring", "Sol Ring", 360)]]);

        var results = Run(frame, detectedCount: 1, identifier, options: new AccuracyHarnessOptions { OkDistance = 270 });

        Assert.Equal(SlotOutcome.Unresolved, results[0].Outcome);
    }

    [Fact]
    public void Run_NoCandidatesReturned_ClassifiesUnresolved()
    {
        var frame = BuildFrame(layout: 1, ("Sol Ring", "oracle-solring", false));
        var identifier = new ScriptedIdentifier([[]]);

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
