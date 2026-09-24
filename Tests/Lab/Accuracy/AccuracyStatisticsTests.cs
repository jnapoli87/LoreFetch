using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// Requirement 1 ("the three buckets must sum to exactly 100% -- assert
/// that in code, not by eye") and the land-exclusion/margin-distribution
/// pieces of `AccuracyStatistics`, tested directly against hand-built
/// `SlotAccuracyResult`s -- no detector, identifier, or image needed, since
/// `AccuracyStatistics.From` is pure aggregation.
public class AccuracyStatisticsTests
{
    [Fact]
    public void From_MixOfOutcomes_HeadlineExcludesLandsAndStretch()
    {
        var results = new List<SlotAccuracyResult>
        {
            Result("normal", isBasicLand: false, SlotOutcome.Correct),
            Result("normal", isBasicLand: false, SlotOutcome.Wrong),
            Result("normal", isBasicLand: false, SlotOutcome.Unresolved),
            Result("land", isBasicLand: true, SlotOutcome.Correct), // excluded from headline: is a land
            Result("stretch", isBasicLand: false, SlotOutcome.Correct), // excluded from headline: not rung=normal
        };

        var stats = AccuracyStatistics.From(results);

        Assert.Equal(1, stats.Headline.Correct);
        Assert.Equal(1, stats.Headline.Wrong);
        Assert.Equal(1, stats.Headline.Unresolved);
        Assert.Equal(0, stats.Headline.DroppedFrame);
        Assert.Equal(3, stats.Headline.Total);
        Assert.Equal(1, stats.ExcludedLandCount);
    }

    [Fact]
    public void From_DroppedFrameSlots_CountAsNoMatchInHeadlineBuckets()
    {
        var results = new List<SlotAccuracyResult>
        {
            Result("normal", false, SlotOutcome.Correct),
            Result("normal", false, SlotOutcome.DroppedFrame),
        };

        var stats = AccuracyStatistics.From(results);

        // The required 3-bucket 100% sum: correct + wrong + no-match ==
        // total, where no-match folds in BOTH unresolved and dropped-frame.
        Assert.Equal(2, stats.Headline.Total);
        Assert.Equal(1, stats.Headline.Correct);
        Assert.Equal(0, stats.Headline.Wrong);
        Assert.Equal(1, stats.Headline.NoMatch);
        Assert.Equal(1, stats.Headline.DroppedFrame);
        Assert.Equal(1.0, stats.Headline.CorrectRate + stats.Headline.WrongRate + stats.Headline.NoMatchRate, precision: 10);
    }

    [Fact]
    public void From_MarginDistribution_OnlyIncludesSlotsWithARank2Candidate()
    {
        var withMargin = new SlotAccuracyResult(
            Slot: MakeSlot("normal", false), SlotOutcome.Correct, Rank1Distance: 40, Rank1OracleId: "x",
            Rank1OracleName: "X", Margin: 220, DetectedCountInFrame: 1);
        var withoutMargin = new SlotAccuracyResult(
            Slot: MakeSlot("normal", false), SlotOutcome.DroppedFrame, Rank1Distance: null, Rank1OracleId: null,
            Rank1OracleName: null, Margin: null, DetectedCountInFrame: 0);

        var stats = AccuracyStatistics.From([withMargin, withoutMargin]);

        Assert.Equal([220], stats.MarginDistribution);
    }

    [Fact]
    public void AssertBucketsSumToTotal_MatchingTotal_DoesNotThrow()
    {
        var buckets = new AccuracyBucketCounts(Correct: 3, Wrong: 1, Unresolved: 2, DroppedFrame: 1);
        buckets.AssertBucketsSumToTotal(expectedTotal: 7);
    }

    [Fact]
    public void AssertBucketsSumToTotal_MismatchedTotal_Throws()
    {
        var buckets = new AccuracyBucketCounts(Correct: 3, Wrong: 1, Unresolved: 2, DroppedFrame: 1);
        Assert.Throws<InvalidOperationException>(() => buckets.AssertBucketsSumToTotal(expectedTotal: 100));
    }

    /// Chaos-test companion to `AssertBucketsSumToTotal`, and my own
    /// un-briefed case: proves the assertion is actually load-bearing
    /// rather than vacuous, by reproducing the exact bug it exists to
    /// catch -- a classification path that drops a result on the floor
    /// (e.g. an `AccuracyBucketCounts.From` that silently skipped one
    /// `SlotOutcome` case) would under-count relative to the real input
    /// size, and this test asserts that mismatch IS caught rather than
    /// silently accepted. Simulated here by comparing the counts From()
    /// actually returns against a DELIBERATELY wrong expected total, the
    /// same failure shape a real regression would produce.
    [Fact]
    public void From_IfABucketWereSilentlyDropped_TheSumAssertionWouldCatchIt()
    {
        var results = new List<SlotAccuracyResult>
        {
            Result("normal", false, SlotOutcome.Correct),
            Result("normal", false, SlotOutcome.Wrong),
            Result("normal", false, SlotOutcome.Unresolved),
            Result("normal", false, SlotOutcome.DroppedFrame),
        };

        var buckets = AccuracyBucketCounts.From(results);

        // The real classification correctly counts all 4 -- confirmed here
        // -- and a hand-built "buggy" count missing one case (as if a
        // future SlotOutcome value were added and forgotten in From's
        // switch) is asserted to fail loudly against the TRUE total.
        buckets.AssertBucketsSumToTotal(results.Count);

        var brokenBuckets = buckets with { DroppedFrame = 0 }; // simulates "forgot to count DroppedFrame"
        Assert.Throws<InvalidOperationException>(() => brokenBuckets.AssertBucketsSumToTotal(results.Count));
    }

    private static SlotAccuracyResult Result(string rung, bool isBasicLand, SlotOutcome outcome) => new(
        MakeSlot(rung, isBasicLand), outcome,
        Rank1Distance: outcome is SlotOutcome.Correct or SlotOutcome.Wrong ? 50 : null,
        Rank1OracleId: outcome is SlotOutcome.Correct or SlotOutcome.Wrong ? "x" : null,
        Rank1OracleName: outcome is SlotOutcome.Correct or SlotOutcome.Wrong ? "X" : null,
        Margin: null,
        DetectedCountInFrame: outcome == SlotOutcome.DroppedFrame ? 0 : 1);

    private static ResolvedGroundTruthRow MakeSlot(string rung, bool isBasicLand) =>
        new(new GroundTruthRow("f.png", 15, 1, 1, "Whatever", rung, "light"), "oracle-x", isBasicLand);
}
