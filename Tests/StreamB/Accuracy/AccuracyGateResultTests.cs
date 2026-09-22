using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.StreamB.Accuracy;

public class AccuracyGateResultTests
{
    [Fact]
    public void Evaluate_WrongCountAtBound_Passes()
    {
        var stats = StatsWithHeadlineWrongCount(2);
        var options = new AccuracyHarnessOptions { MaxWrongAt1AtOkDistance = 2 };

        var gate = AccuracyGateResult.Evaluate(stats, options);

        Assert.True(gate.Passed);
    }

    [Fact]
    public void Evaluate_WrongCountOneOverBound_Fails()
    {
        var stats = StatsWithHeadlineWrongCount(3);
        var options = new AccuracyHarnessOptions { MaxWrongAt1AtOkDistance = 2 };

        var gate = AccuracyGateResult.Evaluate(stats, options);

        Assert.False(gate.Passed);
        Assert.Contains("wrong@1 = 3", gate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_DefaultZeroTolerance_FailsOnAnySingleWrong()
    {
        var stats = StatsWithHeadlineWrongCount(1);

        var gate = AccuracyGateResult.Evaluate(stats, AccuracyHarnessOptions.Default);

        Assert.False(gate.Passed);
    }

    private static AccuracyStatistics StatsWithHeadlineWrongCount(int wrongCount)
    {
        var results = new List<SlotAccuracyResult>();
        for (var i = 0; i < wrongCount; i++)
        {
            results.Add(new SlotAccuracyResult(
                new ResolvedGroundTruthRow(new GroundTruthRow("f.png", 15, 1, 1, "X", "normal", "light"), "oracle-x", false),
                SlotOutcome.Wrong, Rank1Distance: 100, Rank1OracleId: "oracle-wrong", Rank1OracleName: "Wrong", Margin: 5, DetectedCountInFrame: 1));
        }

        return AccuracyStatistics.From(results);
    }
}
