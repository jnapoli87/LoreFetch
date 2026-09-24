using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// `ThresholdsCalibration.Suggest`'s own arithmetic, in isolation --
/// confirming the CODE that would write goodDistance/okDistance is
/// correct, while nothing in this package (or anywhere else in this
/// session) ever calls `Write` against synthetic data. See that type's own
/// doc comment for why.
public class ThresholdsCalibrationTests
{
    [Fact]
    public void Suggest_WithBothCorrectAndWrongSlots_DerivesFromObservedDistances()
    {
        var results = new[]
        {
            Slot(SlotOutcome.Correct, distance: 40),
            Slot(SlotOutcome.Correct, distance: 55), // goodDistance candidate: max correct distance
            Slot(SlotOutcome.Wrong, distance: 210),  // okDistance candidate: min wrong distance - 1
            Slot(SlotOutcome.Wrong, distance: 250),
        };

        var (good, ok) = ThresholdsCalibration.Suggest(results, AccuracyHarnessOptions.Default);

        Assert.Equal(55, good);
        Assert.Equal(209, ok);
    }

    [Fact]
    public void Suggest_NoWrongSlotsObserved_FallsBackToOptionsOkDistancePrior()
    {
        var results = new[] { Slot(SlotOutcome.Correct, distance: 30), Slot(SlotOutcome.Correct, distance: 60) };
        var options = new AccuracyHarnessOptions { OkDistance = 270 };

        var (good, ok) = ThresholdsCalibration.Suggest(results, options);

        Assert.Equal(60, good);
        Assert.Equal(270, ok);
    }

    [Fact]
    public void Suggest_NoCorrectSlotsObserved_GoodDistanceDefaultsToZero()
    {
        var results = new[] { Slot(SlotOutcome.Unresolved, distance: null) };

        var (good, _) = ThresholdsCalibration.Suggest(results, AccuracyHarnessOptions.Default);

        Assert.Equal(0, good);
    }

    private static SlotAccuracyResult Slot(SlotOutcome outcome, int? distance) => new(
        new ResolvedGroundTruthRow(new GroundTruthRow("f.png", 15, 1, 1, "X", "normal", "light"), "oracle-x", false),
        outcome, distance, Rank1OracleId: distance.HasValue ? "some-oracle" : null,
        Rank1OracleName: distance.HasValue ? "Some Card" : null, Margin: null, DetectedCountInFrame: 1);
}
