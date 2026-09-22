namespace LoreFetch.Lab.RoundTrip;

/// Aggregates a `RoundTripGateSummary` into the numbers the brief actually
/// asks to be reported: the overall and per-land/non-land rank-1 rate, and
/// the distance/margin distributions -- computed ONLY over samples whose
/// image was available (`ImageAvailable`), so a run against an incomplete
/// cache never silently folds "missing" into "wrong" or into the
/// distributions.
public sealed record RoundTripGateStatistics(
    int SampleSize,
    int AvailableCount,
    int MissingCount,
    int CorrectCount,
    double Rank1Rate,
    int LandSampleSize,
    int LandCorrectCount,
    double LandRank1Rate,
    int NonLandSampleSize,
    int NonLandCorrectCount,
    double NonLandRank1Rate,
    int OwnDistanceMin,
    double OwnDistanceMean,
    int OwnDistanceMedian,
    int OwnDistanceMax,
    int MarginMin,
    double MarginMean,
    int MarginMedian,
    int MarginMax,
    IReadOnlyList<RoundTripSampleOutcome> Failures,
    IReadOnlyList<string> MissingArtworkIds)
{
    public static RoundTripGateStatistics From(RoundTripGateSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var available = summary.Outcomes.Where(o => o.ImageAvailable).ToList();
        var missing = summary.Outcomes.Where(o => !o.ImageAvailable).ToList();
        var correct = available.Where(o => o.IsCorrect).ToList();
        var failures = available.Where(o => !o.IsCorrect).ToList();

        var lands = available.Where(o => o.IsBasicLand).ToList();
        var landCorrect = lands.Count(o => o.IsCorrect);
        var nonLands = available.Where(o => !o.IsBasicLand).ToList();
        var nonLandCorrect = nonLands.Count(o => o.IsCorrect);

        var ownDistances = correct.Select(o => o.OwnDistance).OrderBy(d => d).ToList();
        var margins = correct.Select(o => o.Margin).OrderBy(m => m).ToList();

        return new RoundTripGateStatistics(
            SampleSize: summary.Outcomes.Count,
            AvailableCount: available.Count,
            MissingCount: missing.Count,
            CorrectCount: correct.Count,
            Rank1Rate: Rate(correct.Count, available.Count),
            LandSampleSize: lands.Count,
            LandCorrectCount: landCorrect,
            LandRank1Rate: Rate(landCorrect, lands.Count),
            NonLandSampleSize: nonLands.Count,
            NonLandCorrectCount: nonLandCorrect,
            NonLandRank1Rate: Rate(nonLandCorrect, nonLands.Count),
            OwnDistanceMin: ownDistances.Count > 0 ? ownDistances[0] : -1,
            OwnDistanceMean: ownDistances.Count > 0 ? ownDistances.Average() : -1,
            OwnDistanceMedian: Median(ownDistances),
            OwnDistanceMax: ownDistances.Count > 0 ? ownDistances[^1] : -1,
            MarginMin: margins.Count > 0 ? margins[0] : -1,
            MarginMean: margins.Count > 0 ? margins.Average() : -1,
            MarginMedian: Median(margins),
            MarginMax: margins.Count > 0 ? margins[^1] : -1,
            Failures: failures,
            MissingArtworkIds: missing.Select(o => o.ArtworkId).ToList());
    }

    private static double Rate(int correct, int total) => total == 0 ? 0 : (double)correct / total;

    private static int Median(List<int> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return -1;
        }

        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 1
            ? sortedValues[mid]
            : (sortedValues[mid - 1] + sortedValues[mid]) / 2;
    }
}
