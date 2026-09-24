namespace LoreFetch.Lab.Accuracy;

/// One row of the per-(height, rung) breakdown table -- correct@1/wrong@1/
/// no-match, broken further into `Unresolved` vs `NoDetection` vs
/// `DroppedFrame` (see `SlotOutcome`'s own doc comment for why those stay
/// distinguishable while all three count as "no-match" for the required
/// 100% sum). `NoDetection` defaults to 0 so existing call sites built
/// before grid inference (this package's Task 2) keep compiling unchanged.
public sealed record AccuracyBucketCounts(int Correct, int Wrong, int Unresolved, int DroppedFrame, int NoDetection = 0)
{
    public int NoMatch => Unresolved + DroppedFrame + NoDetection;

    public int Total => Correct + Wrong + NoMatch;

    public double CorrectRate => Total == 0 ? 0 : (double)Correct / Total;

    public double WrongRate => Total == 0 ? 0 : (double)Wrong / Total;

    public double NoMatchRate => Total == 0 ? 0 : (double)NoMatch / Total;

    /// Requirement 1 ("the three buckets must sum to exactly 100% -- assert
    /// that in code, not by eye"), asserted directly on COUNTS rather than
    /// on percentages (which could be made to "sum to 100.0%" by rounding
    /// even if the underlying counts didn't add up) -- see this type's own
    /// chaos-test note in `AccuracyStatisticsTests`.
    public void AssertBucketsSumToTotal(int expectedTotal)
    {
        var sum = Correct + Wrong + Unresolved + DroppedFrame + NoDetection;
        if (sum != expectedTotal)
        {
            throw new InvalidOperationException(
                $"AccuracyBucketCounts: buckets sum to {sum} slot(s) but {expectedTotal} were classified -- " +
                $"correct={Correct}, wrong={Wrong}, unresolved={Unresolved}, noDetection={NoDetection}, " +
                $"droppedFrame={DroppedFrame}. The three reported buckets (correct/wrong/no-match) must " +
                "account for every classified slot.");
        }
    }

    public static AccuracyBucketCounts From(IEnumerable<SlotAccuracyResult> results)
    {
        var correct = 0;
        var wrong = 0;
        var unresolved = 0;
        var noDetection = 0;
        var dropped = 0;

        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case SlotOutcome.Correct: correct++; break;
                case SlotOutcome.Wrong: wrong++; break;
                case SlotOutcome.Unresolved: unresolved++; break;
                case SlotOutcome.NoDetection: noDetection++; break;
                case SlotOutcome.DroppedFrame: dropped++; break;
                default: throw new ArgumentOutOfRangeException(nameof(results), r.Outcome, "Unknown SlotOutcome.");
            }
        }

        return new AccuracyBucketCounts(correct, wrong, unresolved, dropped, noDetection);
    }
}

/// One breakdown row: a (height, rung) key plus its own bucket counts.
public sealed record AccuracyBreakdownRow(double HeightIn, string Rung, AccuracyBucketCounts Buckets);

/// The full B6 report over one run's `SlotAccuracyResult`s. `Headline` is
/// CLAUDE.md's own required scope for the accuracy figures ("measure
/// accuracy on normal cards only") -- lands EXCLUDED via
/// `ResolvedGroundTruthRow.IsBasicLand` (never via the `rung` string,
/// which is informational labelling only -- reviewer item 5 asks whether
/// the exclusion is "enforced in code rather than remembered", and a
/// `rung == "land"` string check would be exactly the remembered version
/// this flag exists to replace) AND restricted to `rung == "normal"`,
/// matching docs/design/identification.md's own "Done when": "&gt;=90%
/// correct@1 on the real normal-card fixtures". `Breakdown` still reports
/// EVERY row (lands and stretch included) for visibility -- CLAUDE.md:
/// "Capture [lands], run them as a smoke test... exclude them from the
/// table" describes exactly this split, not their absence from the run.
public sealed record AccuracyStatistics(
    AccuracyBucketCounts Headline,
    IReadOnlyList<AccuracyBreakdownRow> Breakdown,
    int ExcludedLandCount,
    IReadOnlyList<int> MarginDistribution,
    int TotalSlotsClassified)
{
    public static AccuracyStatistics From(IReadOnlyList<SlotAccuracyResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var headlineResults = results
            .Where(r => !r.Slot.IsBasicLand && string.Equals(r.Slot.Row.Rung, "normal", StringComparison.Ordinal))
            .ToList();
        var headline = AccuracyBucketCounts.From(headlineResults);
        headline.AssertBucketsSumToTotal(headlineResults.Count);

        var breakdown = results
            .GroupBy(r => (r.Slot.Row.HeightIn, r.Slot.Row.Rung))
            .Select(g => new AccuracyBreakdownRow(g.Key.HeightIn, g.Key.Rung, AccuracyBucketCounts.From(g)))
            .OrderBy(row => row.HeightIn)
            .ThenBy(row => row.Rung, StringComparer.Ordinal)
            .ToList();

        foreach (var row in breakdown)
        {
            var rowCount = results.Count(r => r.Slot.Row.HeightIn == row.HeightIn
                && string.Equals(r.Slot.Row.Rung, row.Rung, StringComparison.Ordinal));
            row.Buckets.AssertBucketsSumToTotal(rowCount);
        }

        var excludedLands = results.Count(r => r.Slot.IsBasicLand);

        // Margin is only meaningful for a slot that was actually queried
        // (DroppedFrame slots never called Identify at all) and got back
        // at least two candidates (rank-2 must exist to have a "best
        // different OracleId" to measure against).
        var margins = results.Where(r => r.Margin.HasValue).Select(r => r.Margin!.Value).OrderBy(m => m).ToList();

        return new AccuracyStatistics(headline, breakdown, excludedLands, margins, results.Count);
    }
}

/// The run-fails-if check: fails when the HEADLINE wrong@1 count exceeds
/// `AccuracyHarnessOptions.MaxWrongAt1AtOkDistance`. Kept as its own tiny
/// type (rather than a bool the caller computes inline) so the CLI command
/// and the real-capture test share exactly one pass/fail rule and one
/// failure message.
public sealed record AccuracyGateResult(bool Passed, string Reason)
{
    public static AccuracyGateResult Evaluate(AccuracyStatistics stats, AccuracyHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(options);

        var wrongCount = stats.Headline.Wrong;
        if (wrongCount <= options.MaxWrongAt1AtOkDistance)
        {
            return new AccuracyGateResult(
                true,
                $"wrong@1 = {wrongCount} (headline: non-land, normal-rung slots), within the bound of " +
                $"{options.MaxWrongAt1AtOkDistance} at OkDistance={options.OkDistance}.");
        }

        return new AccuracyGateResult(
            false,
            $"wrong@1 = {wrongCount} exceeds the bound of {options.MaxWrongAt1AtOkDistance} at " +
            $"OkDistance={options.OkDistance} (headline: non-land, normal-rung slots). A confident wrong " +
            "match is permanent bad inventory (CLAUDE.md) -- this run must not be treated as passing.");
    }
}
