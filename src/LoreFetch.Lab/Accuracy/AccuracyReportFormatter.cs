using System.Text;

namespace LoreFetch.Lab.Accuracy;

/// One text/markdown rendering shared by the `lab accuracy` command's
/// console output, the real-capture test's diagnostic output, and
/// `docs/accuracy.md`'s eventual results table -- one formatter, so the
/// number a reviewer sees on the console is spelled exactly the way the
/// doc will spell it.
public static class AccuracyReportFormatter
{
    public static string Format(
        CorpusCoverage coverage, AccuracyStatistics stats, AccuracyGateResult gate, AccuracyHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();

        sb.AppendLine(coverage.IsPartial
            ? $"** PARTIAL CORPUS ** -- {coverage.Summarize()}"
            : $"Full corpus -- {coverage.Summarize()}");
        sb.AppendLine();

        sb.AppendLine($"Options: OkDistance={options.OkDistance}, MaxWrongAt1AtOkDistance={options.MaxWrongAt1AtOkDistance}, MaxCandidates={options.MaxCandidates}");
        sb.AppendLine();

        sb.AppendLine("Headline (non-land, rung=normal):");
        sb.AppendLine(FormatBucketLine(stats.Headline));
        sb.AppendLine();

        sb.AppendLine($"Lands excluded from the headline: {stats.ExcludedLandCount}");
        sb.AppendLine();

        sb.AppendLine("Full breakdown, per height x rung (lands and stretch cards included -- informational, not headline):");
        sb.AppendLine($"{"Height",8} {"Rung",-8} {"Correct",8} {"Wrong",6} {"NoMatch",8} {"(Unres.",8} {"Dropped)",8} {"Total",6} {"Correct%",9}");
        foreach (var row in stats.Breakdown)
        {
            var b = row.Buckets;
            sb.AppendLine(
                $"{row.HeightIn,6:0.##}in {row.Rung,-8} {b.Correct,8} {b.Wrong,6} {b.NoMatch,8} {b.Unresolved,8} {b.DroppedFrame,9} {b.Total,6} {b.CorrectRate,9:P1}");
        }

        sb.AppendLine();
        sb.AppendLine(FormatMarginSummary(stats.MarginDistribution));
        sb.AppendLine();
        sb.AppendLine($"Gate: {(gate.Passed ? "PASS" : "FAIL")} -- {gate.Reason}");

        return sb.ToString();
    }

    private static string FormatBucketLine(AccuracyBucketCounts b) =>
        $"correct@1={b.Correct} ({b.CorrectRate:P1})  wrong@1={b.Wrong} ({b.WrongRate:P1})  " +
        $"no-match={b.NoMatch} ({b.NoMatchRate:P1}) [unresolved={b.Unresolved}, dropped-frame={b.DroppedFrame}]  total={b.Total}";

    private static string FormatMarginSummary(IReadOnlyList<int> margins)
    {
        if (margins.Count == 0)
        {
            return "Margin distribution: no slot produced a rank-2 candidate (n=0).";
        }

        var min = margins[0];
        var max = margins[^1];
        var mean = margins.Average();
        var median = margins.Count % 2 == 1
            ? margins[margins.Count / 2]
            : (margins[(margins.Count / 2) - 1] + margins[margins.Count / 2]) / 2;

        return $"Margin distribution (rank-1 vs. best different OracleId), n={margins.Count}: " +
               $"min={min}, mean={mean:F1}, median={median}, max={max}.";
    }
}
