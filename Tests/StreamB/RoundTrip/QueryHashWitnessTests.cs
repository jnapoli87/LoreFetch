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

        // Regenerate query hashes for EXACTLY the artwork ids the committed
        // witness names -- never a re-sample. Query hashes depend only on
        // the render bytes and the query transform, not on the index, so
        // this is unaffected by an index rebuild (different artwork
        // count/order) -- unlike `QueryHashWitnessBuilder.Build`, which
        // draws its own seeded sample from whatever index is loaded NOW
        // and would silently compare a DIFFERENT population the moment the
        // index changes (see `QueryHashRegeneration`'s own doc comment).
        var committedArtworkIds = committed.Entries.Select(e => e.ArtworkId).ToList();
        var regenerated = QueryHashWitnessBuilder.BuildForArtworkIds(cacheDir, committedArtworkIds);

        var diff = QueryHashWitnessBuilder.Compare(committed, regenerated);
        var currentToken = ArchitectureProvenance.CurrentToken();

        if (string.Equals(currentToken, committed.MeasuredOn, StringComparison.Ordinal))
        {
            // Same architecture that produced the committed witness: this
            // MUST reproduce bit-exactly, with every artwork's render
            // present. A missing render OR a differing hash here is a real
            // regression in the query path (or the local cache), not an
            // expected divergence -- same severity as a golden-hash
            // mismatch.
            Assert.True(
                diff.MissingCount == 0 && diff.DifferingCount == 0,
                $"{diff.MissingCount} missing, {diff.DifferingCount}/{diff.TotalCompared} query hashes differ " +
                $"from the committed witness on the SAME architecture ({currentToken}) that produced it -- this " +
                $"should be bit-exact with nothing missing. First missing: " +
                $"{diff.MissingArtworkIds.FirstOrDefault() ?? "(none)"}. First difference: " +
                $"{diff.Differences.FirstOrDefault()?.ToString() ?? "(none)"}.");
            return;
        }

        // Foreign architecture: this IS the measurement. Never a failure,
        // never upgraded by LOREFETCH_REQUIRE_REAL -- a cross-architecture
        // divergence here is expected, documented behaviour (CLAUDE.md
        // Real risk #2), not "the real path is untested". Missing and
        // differing are reported SEPARATELY, and the bit-distance stats
        // cover differing hashes only -- conflating "not compared" with
        // "compared and diverged" is exactly the defect this test used to
        // have (an index rebuild made every re-sampled miss read as a
        // hash difference at 0.0 average bit-distance).
        Assert.Skip(
            $"Foreign architecture: witness was measured on \"{committed.MeasuredOn}\", running on " +
            $"\"{currentToken}\". Informational only, not asserted. Compared {diff.TotalCompared}: " +
            $"{diff.MissingCount} missing (render not in cache), {diff.DifferingCount} differing; over the " +
            $"differing hashes -- total bits {diff.TotalDifferingBits}, max bits in one hash " +
            $"{diff.MaxBitsInOneHash}, average bit-distance {diff.AverageBitDistanceAmongDifferences:F1}.");
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
        Assert.Equal(0, diff.MissingCount);
        Assert.Equal(0, diff.DifferingCount);
    }
}

/// Pure, no-I/O tests of `QueryHashWitnessBuilder.Compare`'s missing-vs-
/// differing separation -- the defect this package fixed (B2-witness):
/// "48/50 query hashes differ; average bit-distance among differences:
/// 0.0" was actually 48 artworks the re-sampled comparison never computed
/// at all, misreported as differing hashes at zero bit-distance. These
/// tests pin that separation directly, independent of any real cache or
/// index, so the distinction cannot silently regress even when the
/// cache-gated tests above are skipped on a machine without the cache.
public class QueryHashWitnessCompareTests
{
    private static QueryHashWitnessEntry Entry(string artworkId, string hex) => new(artworkId, hex);

    private static readonly DateTimeOffset MeasuredAt = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    private static QueryHashWitnessDocument Document(params QueryHashWitnessEntry[] entries) =>
        new("x64-windows", "x64-windows (win-x64, Windows)", "deadbeef", entries.Length, 1, MeasuredAt, entries);

    [Fact]
    public void Compare_ArtworkAbsentFromRegeneration_IsReportedAsMissingNotDiffering()
    {
        var committed = Document(Entry("aaa", "ff00"), Entry("bbb", "00ff"));

        // Regeneration found "aaa" but never even attempted "bbb" (its
        // render was not in the cache) -- BuildForArtworkIds' own
        // contract, not a hash that happened to differ.
        var regenerated = new QueryHashRegeneration(
            Entries: [Entry("aaa", "ff00")],
            MissingArtworkIds: ["bbb"]);

        var diff = QueryHashWitnessBuilder.Compare(committed, regenerated);

        Assert.Equal(2, diff.TotalCompared);
        Assert.Equal(1, diff.MissingCount);
        Assert.Equal(["bbb"], diff.MissingArtworkIds);
        Assert.Equal(0, diff.DifferingCount);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void Compare_ArtworkPresentWithADifferentHash_IsReportedAsDifferingNotMissing()
    {
        var committed = Document(Entry("aaa", "ff00"));
        var regenerated = new QueryHashRegeneration(
            Entries: [Entry("aaa", "0f00")], // one hex nibble differs: f (1111) vs 0 (0000) -> 4 bits
            MissingArtworkIds: []);

        var diff = QueryHashWitnessBuilder.Compare(committed, regenerated);

        Assert.Equal(1, diff.TotalCompared);
        Assert.Equal(0, diff.MissingCount);
        Assert.Empty(diff.MissingArtworkIds);
        Assert.Equal(1, diff.DifferingCount);
        Assert.Equal(4, diff.Differences[0].BitDistance);
        Assert.Equal(4, diff.TotalDifferingBits);
        Assert.Equal(4, diff.MaxBitsInOneHash);
        Assert.Equal(4.0, diff.AverageBitDistanceAmongDifferences);
    }

    [Fact]
    public void Compare_MixOfMissingAndDiffering_KeepsBothCountsAndBitStatsSeparate()
    {
        var committed = Document(
            Entry("aaa", "ff00"),  // will match exactly
            Entry("bbb", "ff00"),  // will differ
            Entry("ccc", "ff00")); // will be missing

        var regenerated = new QueryHashRegeneration(
            Entries: [Entry("aaa", "ff00"), Entry("bbb", "0f00")],
            MissingArtworkIds: ["ccc"]);

        var diff = QueryHashWitnessBuilder.Compare(committed, regenerated);

        Assert.Equal(3, diff.TotalCompared);
        Assert.Equal(1, diff.MissingCount);
        Assert.Equal(["ccc"], diff.MissingArtworkIds);
        Assert.Equal(1, diff.DifferingCount);
        Assert.Equal("bbb", diff.Differences[0].ArtworkId);
        // Bit-distance stats must be computed over the ONE differing hash
        // only, never diluted or inflated by the missing entry.
        Assert.Equal(4, diff.TotalDifferingBits);
        Assert.Equal(4, diff.MaxBitsInOneHash);
        Assert.Equal(4.0, diff.AverageBitDistanceAmongDifferences);
    }

    [Fact]
    public void Compare_EverythingMatches_ReportsNeitherMissingNorDiffering()
    {
        var committed = Document(Entry("aaa", "ff00"), Entry("bbb", "00ff"));
        var regenerated = new QueryHashRegeneration(
            Entries: [Entry("aaa", "ff00"), Entry("bbb", "00ff")],
            MissingArtworkIds: []);

        var diff = QueryHashWitnessBuilder.Compare(committed, regenerated);

        Assert.Equal(0, diff.MissingCount);
        Assert.Equal(0, diff.DifferingCount);
        Assert.Equal(0, diff.TotalDifferingBits);
        Assert.Equal(0, diff.MaxBitsInOneHash);
        Assert.Equal(0.0, diff.AverageBitDistanceAmongDifferences);
    }
}
