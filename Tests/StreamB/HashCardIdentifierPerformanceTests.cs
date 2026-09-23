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
    // CLAUDE.md's measured, in-scope index shape (Scryfall's `unique_artwork`
    // after the modern-frame/English/single-faced filter): ~48,700 arts over
    // ~33,600 oracle ids, ~1.45 arts/oracle on average. A 1:1 entries-to-
    // oracles fixture (the original version of this test) hid the ranking
    // cost this test exists to catch -- `RankOracles` scans the ORACLE
    // table, not the entry table, so a fixture with one art per oracle
    // measures the same oracle-table size either way, but a realistic
    // ~1.45x ratio is what the real index actually presents it with.
    private const int OracleCount = 33_600;
    private const int EntryCount = 48_700;
    private const int QueryCount = 9;
    private const double SoftBudgetMs = 50;

    /// Package DH: `DualHypothesisIdentification.Identify` calls
    /// `ICardIdentifier.Identify` up to TWICE per tile (as-detected, then
    /// expanded, unless the expanded quad would leave the frame) -- so a
    /// 9-card cohort's identification cost is up to 18 raw `Identify` calls,
    /// not 9. This measures that directly against `HashCardIdentifier`
    /// (bypassing rectify, same as `QueryCount`'s own test, since this is
    /// about identification cost specifically) rather than assuming 2x the
    /// 9-call figure is representative -- the two hypotheses query the SAME
    /// underlying identifier and index, so there is no reason to expect a
    /// non-linear cost, but this is the actual measurement rather than an
    /// assumption.
    private const int DualHypothesisQueryCount = QueryCount * 2;

    private readonly ITestOutputHelper _output;

    public HashCardIdentifierPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void NineIdentifyCalls_AgainstRealisticIndex_MeasuredAgainstSoftBudget()
    {
        var identifier = BuildRealisticSizedIndex();
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
            $"{QueryCount} Identify calls against a {EntryCount:N0}-entry / {OracleCount:N0}-oracle index: " +
            $"{elapsedMs:F3} ms total, {elapsedMs / QueryCount:F3} ms/query.");

        if (elapsedMs > SoftBudgetMs)
        {
            Assert.Skip(
                $"{QueryCount} Identify calls took {elapsedMs:F3} ms, over the {SoftBudgetMs} ms soft budget -- " +
                "recorded above, not failed. See CLAUDE.md \"Step 7\" (0.243 ms/query baseline).");
        }
    }

    /// Package DH's own cohort-identification cost: 18 raw `Identify` calls
    /// (2 hypotheses x 9 cards), same realistic index/query fixture as
    /// <see cref="NineIdentifyCalls_AgainstRealisticIndex_MeasuredAgainstSoftBudget"/>.
    /// Same soft-skip discipline -- recorded, not gated, since hardware and
    /// build configuration both move this number.
    [Fact]
    public void EighteenIdentifyCalls_DualHypothesisNineCardCohort_MeasuredAgainstSoftBudget()
    {
        var identifier = BuildRealisticSizedIndex();
        var card = BuildQueryCard();

        identifier.Identify(card, maxCandidates: 5); // warm-up, excluded from the measurement

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < DualHypothesisQueryCount; i++)
        {
            identifier.Identify(card, maxCandidates: 5);
        }

        stopwatch.Stop();

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine(
            $"{DualHypothesisQueryCount} Identify calls (package DH: a 9-card cohort's worth of dual-hypothesis " +
            $"identification) against a {EntryCount:N0}-entry / {OracleCount:N0}-oracle index: " +
            $"{elapsedMs:F3} ms total, {elapsedMs / DualHypothesisQueryCount:F3} ms/query, " +
            $"{elapsedMs / QueryCount:F3} ms/cohort-card (dual).");

        if (elapsedMs > SoftBudgetMs)
        {
            Assert.Skip(
                $"{DualHypothesisQueryCount} Identify calls took {elapsedMs:F3} ms, over the {SoftBudgetMs} ms soft " +
                "budget -- recorded above, not failed. See CLAUDE.md \"Step 7\" (0.243 ms/query baseline); dual-" +
                "hypothesis identification is expected to cost roughly 2x that per card.");
        }
    }

    /// `EntryCount` entries over `OracleCount` distinct oracles: every
    /// oracle gets one art, and the first `EntryCount - OracleCount`
    /// oracles get a second -- arts sharing an oracle, as the real index
    /// does, rather than one art per oracle.
    private static HashCardIdentifier BuildRealisticSizedIndex()
    {
        var random = new Random(20260921);
        var entries = new List<HashIndexEntry>(EntryCount);
        var extraArts = EntryCount - OracleCount;
        var entryIndex = 0;

        for (var oracleIndex = 0; oracleIndex < OracleCount; oracleIndex++)
        {
            var artsForThisOracle = oracleIndex < extraArts ? 2 : 1;
            for (var art = 0; art < artsForThisOracle; art++)
            {
                var words = new ulong[CardHash.WordCount];
                for (var w = 0; w < words.Length; w++)
                {
                    words[w] = unchecked((ulong)random.NextInt64());
                }

                entries.Add(new HashIndexEntry(
                    new CardHash(words),
                    OracleId: $"oracle-{oracleIndex:D6}",
                    OracleName: $"Synthetic Card {oracleIndex}",
                    ArtworkId: $"art-{entryIndex:D6}",
                    IsBasicLand: false));
                entryIndex++;
            }
        }

        Debug.Assert(entries.Count == EntryCount, "Fixture entry count drifted from EntryCount.");

        return new HashCardIdentifier(HashFixtures.BuildIndexData(entries));
    }

    private static RectifiedCard BuildQueryCard()
    {
        using var bgr = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 3001);
        return SyntheticImages.ToRectifiedCard(bgr);
    }
}
