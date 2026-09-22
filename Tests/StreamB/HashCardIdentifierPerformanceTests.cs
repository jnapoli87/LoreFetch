using System.Diagnostics;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Timing check for package B3b: CLAUDE.md's "Step 7" measures 0.243 ms per
/// query over 55k entries (2.19 ms for a 9-card cohort) as the argument
/// against any early-rejection shortcut. This test reproduces that
/// measurement against `HashCardIdentifier` itself, at a comparable index
/// size, and records the number rather than gating the build on it --
/// hardware varies, a debug build is slower than Release, and a shared CI
/// runner is noisy. A regression large enough to matter (an accidental
/// quadratic blowup, a boxing allocation inside the per-entry loop) will
/// still show up as a loud number in the test output even on a soft
/// pass/skip.
public class HashCardIdentifierPerformanceTests
{
    private const int EntryCount = 50_000;
    private const int QueryCount = 9;
    private const double SoftBudgetMs = 50;

    private readonly ITestOutputHelper _output;

    public HashCardIdentifierPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void NineIdentifyCalls_Against50kEntryIndex_MeasuredAgainstSoftBudget()
    {
        var identifier = BuildFiftyThousandEntryIndex();
        var card = BuildQueryCard();

        // Warm-up: JIT, first-call allocations -- excluded from the
        // measurement so it reflects steady-state cost, matching how
        // CLAUDE.md's own 0.243 ms/query figure was measured.
        identifier.Identify(card, maxCandidates: 5);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < QueryCount; i++)
        {
            identifier.Identify(card, maxCandidates: 5);
        }

        stopwatch.Stop();

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine(
            $"{QueryCount} Identify calls against a {EntryCount:N0}-entry index: " +
            $"{elapsedMs:F3} ms total, {elapsedMs / QueryCount:F3} ms/query.");

        if (elapsedMs > SoftBudgetMs)
        {
            Assert.Skip(
                $"{QueryCount} Identify calls took {elapsedMs:F3} ms, over the {SoftBudgetMs} ms soft budget -- " +
                "recorded above, not failed. See CLAUDE.md \"Step 7\" (0.243 ms/query baseline).");
        }
    }

    private static HashCardIdentifier BuildFiftyThousandEntryIndex()
    {
        var random = new Random(20260921);
        var entries = new List<HashIndexEntry>(EntryCount);

        for (var i = 0; i < EntryCount; i++)
        {
            var words = new ulong[CardHash.WordCount];
            for (var w = 0; w < words.Length; w++)
            {
                words[w] = unchecked((ulong)random.NextInt64());
            }

            entries.Add(new HashIndexEntry(
                new CardHash(words),
                OracleId: $"oracle-{i:D6}",
                OracleName: $"Synthetic Card {i}",
                ArtworkId: $"art-{i:D6}",
                IsBasicLand: false));
        }

        return new HashCardIdentifier(HashFixtures.BuildIndexData(entries));
    }

    private static RectifiedCard BuildQueryCard()
    {
        using var bgr = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 3001);
        return SyntheticImages.ToRectifiedCard(bgr);
    }
}
