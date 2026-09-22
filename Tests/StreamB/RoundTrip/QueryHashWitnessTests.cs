using LoreFetch.Core.Identification;
using LoreFetch.Lab;
using LoreFetch.Lab.RoundTrip;
using Xunit;

namespace LoreFetch.Tests.StreamB.RoundTrip;

/// The query-hash witness (package B2, third deliverable): measures how
/// much the QUERY side's own `INTER_AREA` step (`CardHasher`'s 32x32
/// resize, step 5) actually diverges between architectures -- a question
/// B1b's golden hashes do not answer, because they are `WindowsOnly` and
/// so never RUN on macOS at all; they prove the REFERENCE side differs
/// (that is WHY they carry that trait), but the query side's magnitude has
/// never been measured on this machine.
///
/// Deliberately NOT `[Trait("Category", "WindowsOnly")]`, unlike
/// `GoldenHashTests` -- copying that trait here would skip this test on
/// the one architecture (arm64-darwin) it most needs to run on, defeating
/// its whole purpose. Instead, the test itself reads the committed
/// witness's OWN recorded `MeasuredOn` and compares it against the
/// CURRENT machine's `ArchitectureProvenance.CurrentToken()` at runtime:
///   - SAME architecture as the committed witness: assert BIT-EXACT
///     equality. A difference here would mean something actually changed
///     on the machine that is supposed to reproduce this exactly -- a real
///     regression, same severity as a golden-hash mismatch.
///   - A DIFFERENT (foreign) architecture: this is the informational
///     measurement the witness exists to take. It is reported via `Skip`
///     (never a failure -- `LOREFETCH_REQUIRE_REAL` does not upgrade this
///     one, because a cross-architecture divergence here is expected,
///     documented behaviour, not "the real path is untested") with the
///     actual bit-difference count and average distance embedded in the
///     skip reason, so the number is still visible in test output rather
///     than silently absorbed by a bare skip.
public class QueryHashWitnessTests
{
    private const int SampleSize = 50;
    private const int Seed = 20260922;

    [Fact]
    public void RegeneratedWitness_MatchesOrExplainsTheCommittedOne()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root to find the committed witness file.");
            return;
        }

        var witnessPath = Path.Combine(repoRoot!, "Tests", "StreamB", "RoundTrip", "round-trip-witness.json");
        if (!File.Exists(witnessPath))
        {
            RealCaptureGate.SkipOrFail($"committed query-hash witness not present at \"{witnessPath}\".");
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
            RealCaptureGate.SkipOrFail(
                $"Scryfall image cache not present at \"{cacheDir}\" -- card imagery can never be committed.");
            return;
        }

        var committed = QueryHashWitnessDocument.Read(witnessPath);
        var index = HashIndexFile.Read(indexPath);

        var cachedCount = Directory.EnumerateFiles(cacheDir, "*.jpg").Count();
        if (cachedCount < index.Entries.Count)
        {
            RealCaptureGate.SkipOrFail(
                $"Scryfall image cache at \"{cacheDir}\" has only {cachedCount}/{index.Entries.Count} images -- " +
                "still populating. Re-run once it finishes.");
            return;
        }

        var indexSha256 = HashIndexFile.ComputeSha256(indexPath);
        var fresh = QueryHashWitnessBuilder.Build(
            index, cacheDir, committed.SampleSize, committed.Seed, indexSha256, DateTimeOffset.UtcNow);

        var diff = QueryHashWitnessBuilder.Compare(committed, fresh);
        var currentToken = ArchitectureProvenance.CurrentToken();

        if (string.Equals(currentToken, committed.MeasuredOn, StringComparison.Ordinal))
        {
            // Same architecture that produced the committed witness: this
            // MUST reproduce bit-exactly. A difference here is a real
            // regression in the query path, not an expected divergence --
            // same severity as a golden-hash mismatch.
            Assert.True(
                diff.DifferingCount == 0,
                $"{diff.DifferingCount}/{diff.TotalCompared} query hashes differ from the committed witness on " +
                $"the SAME architecture ({currentToken}) that produced it -- this should be bit-exact. " +
                $"First difference: {diff.Differences.FirstOrDefault()}");
            return;
        }

        // Foreign architecture: this IS the measurement. Never a failure,
        // never upgraded by LOREFETCH_REQUIRE_REAL -- a cross-architecture
        // divergence here is expected, documented behaviour (CLAUDE.md
        // Real risk #2), not "the real path is untested".
        Assert.Skip(
            $"Foreign architecture: witness was measured on \"{committed.MeasuredOn}\", running on " +
            $"\"{currentToken}\". Informational only, not asserted. {diff.DifferingCount}/{diff.TotalCompared} " +
            $"query hashes differ; average bit-distance among differences: " +
            $"{diff.AverageBitDistanceAmongDifferences:F1}.");
    }

    /// Regenerating the SAME committed witness a second time, from the
    /// same cache and index, must reproduce it bit-for-bit on THIS run --
    /// independent of the cross-architecture question above, this just
    /// confirms the generator itself is deterministic run to run (no
    /// hidden dependency on wall-clock time, thread scheduling, or
    /// anything else nondeterministic).
    [Fact]
    public void Build_CalledTwiceWithTheSameInputs_ProducesTheIdenticalDocument()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root.");
            return;
        }

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        var cacheDir = RoundTripGateTests.ResolveCacheDir();
        if (!File.Exists(indexPath) || !Directory.Exists(cacheDir))
        {
            RealCaptureGate.SkipOrFail("committed index or Scryfall image cache not present.");
            return;
        }

        var index = HashIndexFile.Read(indexPath);
        var cachedCount = Directory.EnumerateFiles(cacheDir, "*.jpg").Count();
        if (cachedCount < index.Entries.Count)
        {
            RealCaptureGate.SkipOrFail($"cache has only {cachedCount}/{index.Entries.Count} images -- still populating.");
            return;
        }

        var indexSha256 = HashIndexFile.ComputeSha256(indexPath);
        var measuredAt = DateTimeOffset.UtcNow;

        var first = QueryHashWitnessBuilder.Build(index, cacheDir, SampleSize, Seed, indexSha256, measuredAt);
        var second = QueryHashWitnessBuilder.Build(index, cacheDir, SampleSize, Seed, indexSha256, measuredAt);

        var diff = QueryHashWitnessBuilder.Compare(first, second);
        Assert.Equal(0, diff.DifferingCount);
    }
}
