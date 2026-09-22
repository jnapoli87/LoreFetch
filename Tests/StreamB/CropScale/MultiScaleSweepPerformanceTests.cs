using System.Diagnostics;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.CropScale;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB.CropScale;

/// Package B5c's cost measurement for the identifier-sweep fix option:
/// the brief's own constraint is explicit -- "measure the new cost and say
/// plainly whether it breaches [the 50 ms / 9-query soft budget]; do not
/// silently relax the budget to fit." This reproduces
/// `HashCardIdentifierPerformanceTests`'s own realistic index shape
/// (48,700 entries / 33,600 oracles, CLAUDE.md's measured in-scope shape)
/// so the two numbers -- baseline `Identify` and a 3-scale sweep -- are
/// directly comparable, not measured under different conditions.
public class MultiScaleSweepPerformanceTests
{
    private const int OracleCount = 33_600;
    private const int EntryCount = 48_700;
    private const int QueryCount = 9;
    private const double SoftBudgetMs = 50;

    private static readonly IReadOnlyList<(float WidthInset, float HeightInset)> ThreeScales =
    [
        (0f, 0f),
        (-0.05f, -0.05f),
        (-0.10f, -0.10f),
    ];

    private readonly ITestOutputHelper _output;

    public MultiScaleSweepPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void NineSweptQueries_AgainstRealisticIndex_MeasuredAgainstSoftBudget()
    {
        var index = BuildRealisticSizedIndexData();
        using var card = BuildQueryColorCard();

        // Warm-up, matching HashCardIdentifierPerformanceTests's own
        // pattern -- excluded from the measurement.
        MultiScaleSweepExperiment.IdentifyWithSweep(index, card, "oracle-000000", "art-000000", ThreeScales);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < QueryCount; i++)
        {
            MultiScaleSweepExperiment.IdentifyWithSweep(index, card, "oracle-000000", "art-000000", ThreeScales);
        }

        stopwatch.Stop();

        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine(
            $"{QueryCount} 3-scale-swept queries against a {EntryCount:N0}-entry / {OracleCount:N0}-oracle index: " +
            $"{elapsedMs:F3} ms total, {elapsedMs / QueryCount:F3} ms/query -- " +
            $"{(elapsedMs > SoftBudgetMs ? "BREACHES" : "within")} the {SoftBudgetMs} ms soft budget " +
            "(HashCardIdentifierPerformanceTests's own budget for un-swept queries).");

        // Reported, never silently relaxed -- per this package's own
        // brief. This test always PASSES (it is a measurement, exactly
        // like HashCardIdentifierPerformanceTests's own soft-skip), so the
        // number in the output above -- not this assertion -- is the
        // actual finding for the recommendation.
        Assert.True(elapsedMs >= 0);
    }

    /// Same generation as `HashCardIdentifierPerformanceTests.BuildRealisticSizedIndex`,
    /// duplicated locally (rather than promoting a private helper to
    /// shared/internal) to keep this package's footprint inside its own
    /// new file, per this package's own write-scope discipline -- both
    /// copies must stay in sync if the realistic shape is ever revised, and
    /// the doc comment on each says so.
    private static HashIndexData BuildRealisticSizedIndexData()
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

        var oracleTable = new List<OracleEntry>(OracleCount);
        for (var oracleIndex = 0; oracleIndex < OracleCount; oracleIndex++)
        {
            oracleTable.Add(new OracleEntry($"oracle-{oracleIndex:D6}", $"Synthetic Card {oracleIndex}"));
        }

        return new HashIndexData(entries, oracleTable);
    }

    private static Mat BuildQueryColorCard()
    {
        return SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 3001);
    }
}
