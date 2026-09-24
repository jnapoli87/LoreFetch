using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.Collection;

/// The dedup key's `Condition` half, and the `ArtworkId` fold, are both
/// unreachable through `CommitCohortAsync` in v1 — `CohortTile` carries no
/// condition, so a bug conflating `null` with `""` in the key, or making
/// `ArtworkId` last-write-wins, would pass every commit-path test and be
/// caught by none of them (measured on the Stream 0 stub: 79 unrelated
/// tests stayed green). Every test here is therefore driven from a WRITTEN
/// file — `NativeCsvCodec.WriteAsync` followed by `ReadAsync` — never from
/// an in-memory merge call, so it exercises the actual reader path a real
/// hand-edited or re-saved collection.csv would hit.
public class NativeCsvCodecMergeTests
{
    private static async Task<IReadOnlyList<CollectionRow>> WriteThenReadAsync(
        IReadOnlyList<CollectionRow> rows, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var writer = new StringWriter();
        await NativeCsvCodec.WriteAsync(writer, rows, CancellationToken.None);
        return await NativeCsvCodec.ReadAsync(new StringReader(writer.ToString()), logger, CancellationToken.None);
    }

    [Fact]
    public async Task TwoRowsWithBlankCondition_MergeIntoOneWithSummedQuantity()
    {
        var a = TestSupport.Row(quantity: 1, condition: null);
        var b = TestSupport.Row(quantity: 1, condition: null);

        var result = await WriteThenReadAsync([a, b]);

        var row = Assert.Single(result);
        Assert.Equal(2, row.Quantity);
        Assert.Null(row.Condition);
    }

    [Fact]
    public async Task BlankAndNonBlankCondition_AreDistinctKeys_DoNotMerge()
    {
        var blank = TestSupport.Row(quantity: 1, condition: null);
        var graded = TestSupport.Row(quantity: 1, condition: "NM");

        var result = await WriteThenReadAsync([blank, graded]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Condition is null && r.Quantity == 1);
        Assert.Contains(result, r => r.Condition == "NM" && r.Quantity == 1);
    }

    [Fact]
    public async Task TwoDifferentNonBlankConditions_AreDistinctKeys_DoNotMerge()
    {
        var lp = TestSupport.Row(quantity: 1, condition: "LP");
        var nm = TestSupport.Row(quantity: 1, condition: "NM");

        var result = await WriteThenReadAsync([lp, nm]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task SameConditionTwice_MergesAcrossThreeRows()
    {
        var rows = new[]
        {
            TestSupport.Row(quantity: 1, condition: "NM"),
            TestSupport.Row(quantity: 3, condition: "NM"),
            TestSupport.Row(quantity: 5, condition: "NM"),
        };

        var result = await WriteThenReadAsync(rows);

        var row = Assert.Single(result);
        Assert.Equal(9, row.Quantity);
    }

    [Fact]
    public async Task DifferentOracleId_NeverMergesEvenWithSameCondition()
    {
        var forest = TestSupport.Row(oracleId: "11111111-1111-1111-1111-111111111111", condition: null);
        var island = TestSupport.Row(oracleId: "22222222-2222-2222-2222-222222222222", condition: null);

        var result = await WriteThenReadAsync([forest, island]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ArtworkId_AgreesAcrossDuplicates_IsKeptOnMerge()
    {
        var a = TestSupport.Row(quantity: 1, artworkId: "art-1");
        var b = TestSupport.Row(quantity: 1, artworkId: "art-1");

        var result = await WriteThenReadAsync([a, b]);

        Assert.Equal("art-1", Assert.Single(result).ArtworkId);
    }

    [Fact]
    public async Task ArtworkId_DisagreesAcrossDuplicates_IsNulledOnMerge()
    {
        // Agree-or-null, never last-write-wins: a wrong printing id reads as
        // confidently right, which is worse than an honest "don't know".
        var a = TestSupport.Row(quantity: 1, artworkId: "art-1");
        var b = TestSupport.Row(quantity: 1, artworkId: "art-2");

        var result = await WriteThenReadAsync([a, b]);

        Assert.Null(Assert.Single(result).ArtworkId);
    }

    [Fact]
    public async Task ArtworkId_OneNullOneValue_IsNulledOnMerge()
    {
        var manual = TestSupport.Row(quantity: 1, artworkId: null);
        var hashed = TestSupport.Row(quantity: 1, artworkId: "art-1");

        var result = await WriteThenReadAsync([manual, hashed]);

        Assert.Null(Assert.Single(result).ArtworkId);
    }

    [Fact]
    public async Task ArtworkId_BothNull_StaysNullOnMerge()
    {
        var a = TestSupport.Row(quantity: 1, artworkId: null, source: RowSource.Manual, bestMatchDistance: null);
        var b = TestSupport.Row(quantity: 1, artworkId: null, source: RowSource.Manual, bestMatchDistance: null);

        var result = await WriteThenReadAsync([a, b]);

        Assert.Null(Assert.Single(result).ArtworkId);
    }

    [Fact]
    public async Task Merge_KeepsTheLatestLastScannedAt()
    {
        var older = TestSupport.Row(quantity: 1, lastScannedAt: TestSupport.SampleTimestamp);
        var newer = TestSupport.Row(quantity: 1, lastScannedAt: TestSupport.SampleTimestamp.AddDays(1));

        // Write in the "wrong" order to prove the merge picks the latest
        // timestamp by value, not by write/appearance order.
        var result = await WriteThenReadAsync([newer, older]);

        var row = Assert.Single(result);
        Assert.Equal(TestSupport.SampleTimestamp.AddDays(1), row.LastScannedAt);
        Assert.Equal(2, row.Quantity);
    }

    [Fact]
    public async Task Merge_IsLogged()
    {
        var logger = new CapturingLogger();
        var a = TestSupport.Row(quantity: 1);
        var b = TestSupport.Row(quantity: 1);

        await WriteThenReadAsync([a, b], logger);

        Assert.Contains(logger.Messages, m => m.Contains("Merged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoDuplicates_NothingIsLogged()
    {
        var logger = new CapturingLogger();
        var a = TestSupport.Row(oracleId: "11111111-1111-1111-1111-111111111111");
        var b = TestSupport.Row(oracleId: "22222222-2222-2222-2222-222222222222");

        await WriteThenReadAsync([a, b], logger);

        Assert.Empty(logger.Messages);
    }
}
