using LoreFetch.Core.Identification;
using LoreFetch.Lab;
using LoreFetch.Lab.RoundTrip;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamB.RoundTrip;

/// Package B2 -- "the one gate that matters most" (CLAUDE.md): a Scryfall
/// render, presented through the SHIPPING query path
/// (`ICardIdentifier.Identify`, exactly as the scanner calls it), must
/// retrieve its own artwork at rank 1, at a small, recorded, stable
/// distance.
///
/// Artifact-gated (mirrors `Tests/Integration`'s `RealArtifactGate` /
/// `RealCaptureGate` in this same project, orchestration finding V7): a
/// missing or incomplete external image cache is a SKIP with a reason,
/// never a failure, under the CI default -- card imagery can never be
/// committed (CLAUDE.md "Never commit card imagery"), so this can only run
/// for real on a machine that has pulled the cache. Set
/// `LOREFETCH_REQUIRE_REAL=1` to turn that same skip into a failure on a
/// machine that DOES have the artifacts.
///
/// Deliberately does NOT write `data/index/thresholds.json` itself. That
/// file is generated once, by hand, via `lab round-trip-gate --out
/// data/index/thresholds.json` (mirroring B4d's own precedent: the
/// committed index is likewise built by a deliberate human-run command,
/// never by the test suite) -- a test that rewrote a committed data file
/// as a side effect of an ordinary local run would make "the one gate that
/// matters most" driftable by anyone who happens to run `dotnet test` with
/// the cache present, which is exactly the kind of silent change CLAUDE.md
/// warns against. This test's job is VERIFICATION: it performs the SAME
/// measurement, every real run, and asserts against fixed, previously-
/// measured bounds (the same "measure once, hardcode the bound" pattern
/// B1a's own invariant tests use) -- not against whatever the committed
/// file currently happens to say.
public class RoundTripGateTests
{
    /// B4d's committed index, pinned. A wrong SHA here means either the
    /// index was rebuilt/corrupted since this test was written (this
    /// package must NOT rebuild or modify it -- see this package's own
    /// brief) or something else entirely is sitting at that path -- either
    /// way, the gate must refuse to measure against an index it cannot
    /// verify rather than silently reporting numbers against the wrong
    /// data. See RoundTripGateTests_ChaosNotes.md-equivalent note in the
    /// implementation report for the chaos verification of this exact
    /// check (a deliberately wrong constant here was confirmed to fail
    /// loudly, then reverted).
    private const string ExpectedIndexSha256 = "6495314eb3e5f37e3c1bd5830aa506d6efc617e23303c46f87851edef09e4dd3";

    /// Measured 2026-09-22 on arm64-darwin against the real 48,750-artwork
    /// index and the full real cache: own-distance max was well under 100
    /// across the full 200-artwork sample (lands and non-lands both). This
    /// bound carries deliberate headroom above that measurement -- the
    /// same "measure once, then pin a bound with headroom" pattern B1a's
    /// own invariant tests use (CLAUDE.md's own gate spec: "Not a ≈ 0" but
    /// "small, recorded, stable") -- so the test is a real regression
    /// guard, not a number tied to the exact decimal this run happened to
    /// produce.
    private const int MaxAcceptableOwnDistance = 150;

    /// CLAUDE.md/orchestration-plan.md B2: "If the rank-1 artwork rate is
    /// below 99%, stop and ask" -- a project-level gate, not a tuning knob.
    private const double MinAcceptableRank1Rate = 0.99;

    [Fact]
    public void RoundTripGate_RealScryfallRenders_RetrieveTheirOwnArtworkAtRank1()
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

        var cacheDir = ResolveCacheDir();
        if (!Directory.Exists(cacheDir))
        {
            RealCaptureGate.SkipOrFail(
                $"Scryfall image cache not present at \"{cacheDir}\" -- card imagery can never be committed.");
            return;
        }

        var index = HashIndexFile.Read(indexPath);

        // "Check its file count before running the gate; if it is short,
        // wait and re-check rather than concluding images are missing" --
        // this package's own brief. A partially-populated cache is a SKIP
        // with a reason (how far along it is), not a false "images are
        // missing" failure and not a silent partial measurement against
        // whatever happens to be there yet.
        var cachedCount = Directory.EnumerateFiles(cacheDir, "*.jpg").Count();
        if (cachedCount < index.Entries.Count)
        {
            RealCaptureGate.SkipOrFail(
                $"Scryfall image cache at \"{cacheDir}\" has only {cachedCount}/{index.Entries.Count} images -- " +
                "still populating. Re-run once it finishes.");
            return;
        }

        var indexSha256 = HashIndexFile.ComputeSha256(indexPath);
        Assert.Equal(ExpectedIndexSha256, indexSha256);

        var identifier = HashCardIdentifier.Load(indexPath, NullLoggerFactory.Instance);
        var options = new RoundTripGateOptions
        {
            CacheDir = cacheDir,
            LandSampleSize = RoundTripGateCommand.DefaultLandSampleSize,
            NonLandSampleSize = RoundTripGateCommand.DefaultNonLandSampleSize,
            Seed = RoundTripGateCommand.DefaultSeed,
        };

        var summary = RoundTripGateRunner.Run(index, identifier, options);
        var stats = RoundTripGateStatistics.From(summary);

        Assert.True(
            stats.MissingCount == 0,
            $"{stats.MissingCount} sampled artwork(s) had no usable cache image despite the cache reporting " +
            $"{cachedCount}/{index.Entries.Count} files present: {string.Join(", ", stats.MissingArtworkIds)}");

        if (stats.Rank1Rate < MinAcceptableRank1Rate)
        {
            var failureLines = stats.Failures.Select(f =>
                $"  {f.ArtworkId} (oracle {f.OracleId}, land={f.IsBasicLand}): own distance {f.OwnDistance}, " +
                $"rank1 -> {f.Rank1ArtworkId ?? "(none)"} at distance {f.Rank1Distance}");

            Assert.Fail(
                $"STOP-AND-ASK (CLAUDE.md / orchestration-plan.md B2): rank-1 ArtworkId match rate " +
                $"{stats.Rank1Rate:P2} ({stats.CorrectCount}/{stats.AvailableCount}) is below the " +
                $"{MinAcceptableRank1Rate:P0} floor. This is a project-level gate -- the user decides what " +
                $"happens next, not this test. Failures:\n{string.Join('\n', failureLines)}");
        }

        // The bound, not equality (CLAUDE.md "The one gate that matters
        // most": "Not ≈ 0 ... demanding zero would mean deleting the
        // mechanism that makes a webcam frame match a print render").
        // `OwnDistance` is the distance to the QUERIED artwork's own
        // entry regardless of whether Identify's collapse happened to
        // rank a sibling art higher, so this checks every available
        // outcome, not just the correct ones.
        foreach (var outcome in summary.Outcomes.Where(o => o.ImageAvailable))
        {
            Assert.True(
                outcome.OwnDistance <= MaxAcceptableOwnDistance,
                $"{outcome.ArtworkId}: own distance {outcome.OwnDistance} exceeds the {MaxAcceptableOwnDistance} bound.");

            // Cross-check between the two independent computations this
            // measurement makes (chaos-found, not brief-mandated -- see
            // this package's implementation report): `Rank1Distance` comes
            // from `ICardIdentifier.Identify` itself; `OwnDistance` comes
            // from `RoundTripMeasurement`'s own raw scan, which calls
            // `QueryTransform.Prepare` directly. For a CORRECT match the
            // two are measuring the identical quantity -- the distance
            // from this query's hash to its own entry's hash -- via two
            // separate call sites that must both be exercising the SAME
            // query transform. If `HashCardIdentifier` ever diverged
            // internally (e.g. accidentally routed through the
            // reference-side blur+downsample instead), its own reported
            // distance would drop toward 0 -- a suspiciously PERFECT
            // match, made possible only because the render being queried
            // and its own index entry would then have been produced by
            // the IDENTICAL reference-side pipeline -- while this
            // independent raw scan's `OwnDistance` would stay at its
            // normal, non-zero value, and this assertion catches exactly
            // that gap. Confirmed: temporarily swapping the query-side
            // transform for the reference-side one inside
            // `HashCardIdentifier.HashBothOrientations` left the primary
            // rank-1/bound assertions above completely unaffected (the
            // render still "matched itself" at Rank1Distance 0, even
            // BETTER by that number alone) but made THIS assertion fail
            // immediately (0 vs 19-52) -- see the implementation report.
            if (outcome.IsCorrect)
            {
                Assert.Equal(outcome.OwnDistance, outcome.Rank1Distance);
            }
        }

        Assert.True(stats.MarginMean > 0, $"mean margin {stats.MarginMean:F1} should be comfortably positive.");
    }

    /// The default cache path is Windows-shaped in the plan
    /// (`C:\LoreFetchData\scryfall-cache`) and Mac-shaped in the
    /// orchestration handoff (`~/LoreFetchData/scryfall-cache`) -- a
    /// parameter, per this package's own brief ("Take the cache path as a
    /// parameter with that as the default, never a hardcoded constant"),
    /// overridable via `LOREFETCH_SCRYFALL_CACHE` for a machine whose
    /// cache lives somewhere else.
    internal static string ResolveCacheDir()
    {
        var overridePath = Environment.GetEnvironmentVariable("LOREFETCH_SCRYFALL_CACHE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "LoreFetchData", "scryfall-cache");
    }
}
