using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Scanning;
using LoreFetch.Lab;
using LoreFetch.Lab.Accuracy;
using LoreFetch.Tests.Lab;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// The REAL corpus path: `test-images/ground-truth.csv` +
/// `test-images/fixtures/`. Artifact-gated exactly like
/// `RealCaptureGate`/`CropScale/CropScaleRealFrameTests` -- skips with a
/// clear reason when H3's corpus is absent (the CI default, and the
/// current state of this worktree: neither file exists yet, per this
/// package's own brief), and the SAME condition is a hard failure under
/// `LOREFETCH_REQUIRE_REAL=1` so a machine that DOES have the corpus can
/// never quietly skip the real check.
///
/// H3 delivers the corpus in batches (batch A: six 3x3 frames at 15in,
/// light mat, before the rest) -- this test runs on WHATEVER subset of
/// ground-truth.csv actually has a fixture file on disk
/// (`AccuracyCorpusLoader.SplitByPresence`), and prints the coverage that
/// subset represents so a partial run is never presentable as the full
/// result.
public class AccuracyHarnessRealCaptureTests
{
    private readonly ITestOutputHelper _output;

    public AccuracyHarnessRealCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Run_AgainstRealH3Corpus_ReportsAccuracyAndEvaluatesTheGate()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root.");
            return;
        }

        var groundTruthPath = Path.Combine(repoRoot!, "test-images", "ground-truth.csv");
        var fixturesDir = Path.Combine(repoRoot!, "test-images", "fixtures");
        if (!File.Exists(groundTruthPath) || !Directory.Exists(fixturesDir))
        {
            RealCaptureGate.SkipOrFail(
                $"H3 fixture corpus not present -- \"{groundTruthPath}\" and/or \"{fixturesDir}\" are missing. " +
                "This is expected until the user finishes capturing test-images/fixtures/ (gated on H3); " +
                "the harness itself is covered by AccuracyHarnessSyntheticTests instead.");
            return;
        }

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            RealCaptureGate.SkipOrFail($"committed hash index not present at \"{indexPath}\".");
            return;
        }

        var index = HashIndexFile.Read(indexPath);
        var identifier = new HashCardIdentifier(index);

        var rawRows = GroundTruthCsvReader.Read(groundTruthPath);
        var resolved = GroundTruthOracleLookup.Resolve(index, rawRows);
        var allFrames = GroundTruthFrame.GroupByFile(resolved);

        var (found, missing, coverage) = AccuracyCorpusLoader.SplitByPresence(repoRoot!, allFrames);
        _output.WriteLine(coverage.Summarize());
        if (missing.Count > 0)
        {
            _output.WriteLine($"{missing.Count} ground-truth frame(s) have no fixture file on disk yet -- not run, not counted as failures.");
        }

        if (found.Count == 0)
        {
            RealCaptureGate.SkipOrFail(
                $"ground-truth.csv lists {allFrames.Count} frame(s) but none have a fixture file on disk yet.");
            return;
        }

        // OkDistance comes from the committed thresholds.json (B6's real-
        // corpus-calibrated value), not the AccuracyHarnessOptions.Default
        // CardSpotter-prior placeholder (270) -- DECISIONS.md/CONTRACTS.md:
        // "nothing may hardcode a distance; the thresholds file is the one
        // source of truth." The committed index exists by this point
        // (checked above), and thresholds.json ships alongside it, so this
        // is expected to load for real here, not fall back.
        var thresholdsPath = Path.Combine(repoRoot!, "data", "index", "thresholds.json");
        var options = AccuracyHarnessOptions.Default with { OkDistance = ThresholdsFile.Load(thresholdsPath).OkDistance };
        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var rectifier = new PerspectiveRectifier();

        var allResults = new List<SlotAccuracyResult>();
        foreach (var frame in found)
        {
            var path = AccuracyCorpusLoader.ResolveFixturePath(repoRoot!, frame.File);
            using var mat = AccuracyCorpusLoader.LoadFixtureMat(path);
            using var cameraFrame = FrameMat.FromMat(mat);
            allResults.AddRange(AccuracyFrameRunner.Run(frame, cameraFrame, detector, rectifier, identifier, options));
        }

        var stats = AccuracyStatistics.From(allResults);
        var gate = AccuracyGateResult.Evaluate(stats, options);

        _output.WriteLine(AccuracyReportFormatter.Format(coverage, stats, gate, options));

        if (!gate.Passed)
        {
            Assert.Fail(gate.Reason);
        }
    }
}
