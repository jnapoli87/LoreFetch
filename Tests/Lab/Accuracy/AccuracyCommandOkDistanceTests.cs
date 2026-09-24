using LoreFetch.Lab;
using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// Package B6-thresholds: `lab accuracy`'s `--ok-distance` default must
/// come from the committed `data/index/thresholds.json`, not the hardcoded
/// `AccuracyHarnessOptions.Default.OkDistance` (270, a documented
/// CardSpotter-upstream prior -- see that type's own doc comment) --
/// CLAUDE.md/CONTRACTS.md: "nothing may hardcode a distance; the
/// thresholds file is the one source of truth." Exercises
/// `AccuracyCommand.ResolveOkDistance` directly (made `internal` for this
/// purpose -- `InternalsVisibleTo("LoreFetch.Tests.Lab")` already
/// covers this project) against throwaway temp directories, rather than
/// the full CLI end to end, because a full `accuracy` run only prints its
/// resolved `OkDistance` once it also finds `test-images/ground-truth.csv`
/// + fixtures, which are real card imagery and can never be committed
/// (CLAUDE.md "Never commit card imagery") -- this logic must be testable
/// without that corpus.
public class AccuracyCommandOkDistanceTests
{
    [Fact]
    public void ResolveOkDistance_ExplicitOverrideGiven_WinsOverTheThresholdsFile()
    {
        var repoRoot = Directory.CreateTempSubdirectory("lorefetch-accuracy-okdistance-").FullName;
        try
        {
            WriteThresholds(repoRoot, okDistance: 240);

            var resolved = AccuracyCommand.ResolveOkDistance(repoRoot, overrideValue: 999);

            Assert.Equal(999, resolved);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveOkDistance_NoOverride_ReadsTheCommittedThresholdsFile()
    {
        var repoRoot = Directory.CreateTempSubdirectory("lorefetch-accuracy-okdistance-").FullName;
        try
        {
            WriteThresholds(repoRoot, okDistance: 240);

            var resolved = AccuracyCommand.ResolveOkDistance(repoRoot, overrideValue: null);

            // The whole point: 240 (this session's B6 calibration), NOT
            // AccuracyHarnessOptions.Default.OkDistance (270).
            Assert.Equal(240, resolved);
            Assert.NotEqual(AccuracyHarnessOptions.Default.OkDistance, resolved);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveOkDistance_ThresholdsFileMissing_FallsBackToTheDocumentedDefault()
    {
        var repoRoot = Directory.CreateTempSubdirectory("lorefetch-accuracy-okdistance-").FullName;
        try
        {
            // No data/index/thresholds.json written at all.
            var resolved = AccuracyCommand.ResolveOkDistance(repoRoot, overrideValue: null);

            Assert.Equal(AccuracyHarnessOptions.Default.OkDistance, resolved);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveOkDistance_ThresholdsFileMalformed_FallsBackToTheDocumentedDefault()
    {
        var repoRoot = Directory.CreateTempSubdirectory("lorefetch-accuracy-okdistance-").FullName;
        try
        {
            var indexDir = Path.Combine(repoRoot, "data", "index");
            Directory.CreateDirectory(indexDir);
            File.WriteAllText(Path.Combine(indexDir, "thresholds.json"), "{ not valid json");

            var resolved = AccuracyCommand.ResolveOkDistance(repoRoot, overrideValue: null);

            Assert.Equal(AccuracyHarnessOptions.Default.OkDistance, resolved);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static void WriteThresholds(string repoRoot, int okDistance)
    {
        var indexDir = Path.Combine(repoRoot, "data", "index");
        Directory.CreateDirectory(indexDir);
        File.WriteAllText(Path.Combine(indexDir, "thresholds.json"), $$"""
            {
              "formatVersion": 1,
              "goodDistance": 208,
              "okDistance": {{okDistance}},
              "referenceFloor": 61,
              "indexArtworkCount": 47418,
              "indexSha256": "test-sha",
              "measuredAt": "2026-09-22T00:00:00+00:00",
              "notes": "test fixture"
            }
            """);
    }
}
