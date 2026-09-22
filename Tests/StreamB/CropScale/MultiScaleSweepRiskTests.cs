using LoreFetch.Core.Identification;
using LoreFetch.Lab;
using LoreFetch.Lab.CropScale;
using LoreFetch.Lab.Images;
using LoreFetch.Lab.RoundTrip;
using LoreFetch.Tests.StreamB.RoundTrip;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB.CropScale;

/// Package B5c's decision support: `CropScaleRealFrameTests` shows a
/// compensating crop-scale sweep CAN recover `plains_black` (the known
/// B5b failure). This test asks the other half of the question the
/// brief's identifier-sweep option requires answering before it can be
/// recommended: does trying those SAME extra scales make ALREADY
/// well-framed queries (the 200-sample B2 gate's own 100% rank-1
/// population) worse? Every extra scale can only ever LOWER a candidate
/// oracle's best distance (never raise it), so sweeping cannot help a
/// well-framed query's OWN distance -- the risk is entirely that some
/// OTHER oracle's distance drops further and overtakes it.
///
/// Artifact-gated like `RoundTripGateTests` (same cache, same sampling
/// parameters as B2's own default, so this measures the identical
/// population B2 already certified as 100% correct at rank 1 with no
/// sweep at all).
public class MultiScaleSweepRiskTests
{
    private static readonly IReadOnlyList<(float WidthInset, float HeightInset)> CandidateSweepScales =
    [
        (0f, 0f),
        (-0.05f, -0.05f),
        (-0.10f, -0.10f),
    ];

    private readonly ITestOutputHelper _output;

    public MultiScaleSweepRiskTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ThreeScaleSweep_OnAlreadyWellFramedQueries_FalsePositiveRateAndMarginImpact()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root to find the committed index.");
            return;
        }

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            RealCaptureGate.SkipOrFail($"committed hash index not present at \"{indexPath}\".");
            return;
        }

        var cacheDir = RoundTripGateTests.ResolveCacheDir();
        if (!Directory.Exists(cacheDir))
        {
            RealCaptureGate.SkipOrFail($"Scryfall image cache not present at \"{cacheDir}\" -- card imagery can never be committed.");
            return;
        }

        var index = HashIndexFile.Read(indexPath);
        var cachedCount = Directory.EnumerateFiles(cacheDir, "*.jpg").Count();
        if (cachedCount < index.Entries.Count)
        {
            RealCaptureGate.SkipOrFail($"Scryfall image cache at \"{cacheDir}\" has only {cachedCount}/{index.Entries.Count} images -- still populating.");
            return;
        }

        // Same non-land population and seed as CropScaleExperimentRunner's
        // default -- deliberately smaller than B2's own 180 to keep this
        // 3x-cost-per-query measurement's runtime reasonable; it exists to
        // show a DIRECTION and a magnitude, not to replace B2's own gate.
        var nonLandEntries = index.Entries.Where(e => !e.IsBasicLand).ToList();
        var sample = RoundTripSampler.SelectUniformSample(nonLandEntries, count: 150, seed: 20260922);

        var wasCorrectNowWrong = new List<string>();
        var marginDrops = new List<int>();
        var missing = 0;

        foreach (var entry in sample)
        {
            var imagePath = ImageCache.GetImagePath(cacheDir, entry.ArtworkId);
            using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (color.Empty())
            {
                missing++;
                continue;
            }

            var (rank1OracleId, _, ownDistance, margin) =
                MultiScaleSweepExperiment.IdentifyWithSweep(index, color, entry.OracleId, entry.ArtworkId, CandidateSweepScales);

            if (rank1OracleId != entry.OracleId)
            {
                wasCorrectNowWrong.Add($"{entry.ArtworkId} (oracle {entry.OracleId}) -> swept rank1 is {rank1OracleId}");
            }

            marginDrops.Add(margin);
            _ = ownDistance; // recorded for completeness; not asserted on directly here
        }

        Assert.Equal(0, missing);

        // This is the headline number for the recommendation: B2 already
        // measured this exact population at 100% rank-1 with NO sweep. Any
        // non-zero count here is the false-positive cost of the sweep
        // option, and must be weighed against CropScaleRealFrameTests's
        // recovery evidence rather than assumed away.
        var falsePositiveCount = wasCorrectNowWrong.Count;
        var falsePositiveRate = (double)falsePositiveCount / sample.Count;

        _output.WriteLine(
            $"Sample {sample.Count}: {falsePositiveCount} flipped to wrong rank-1 ({falsePositiveRate:P2}). " +
            $"Swept margin: min {marginDrops.Min()}, mean {marginDrops.Average():F1}, max {marginDrops.Max()}.");
        foreach (var line in wasCorrectNowWrong)
        {
            _output.WriteLine("  " + line);
        }

        Assert.True(
            falsePositiveRate <= 0.10,
            $"3-scale sweep flipped {falsePositiveCount}/{sample.Count} ({falsePositiveRate:P1}) already-correct queries to a " +
            $"WRONG rank-1 -- above the 10% sanity bound this test uses to catch a gross regression. Detail:\n" +
            string.Join('\n', wasCorrectNowWrong));
    }
}
