using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.Collection;

/// docs/TESTING.md's "CSV store" case: "a malformed line is reported, not
/// silently dropped." Every case here must throw NativeCsvFormatException,
/// never skip the offending line and return the rest.
public class NativeCsvCodecMalformedTests
{
    private const string Header = "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId\r\n";

    [Fact]
    public async Task RowWithTooFewFields_ThrowsAndNamesTheLine()
    {
        var text = Header + "id1,Forest,1,,2026-09-21T10:00:00.0000000+00:00,42,Hash\r\n"; // ArtworkId column missing on this row

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("Line 2", ex.Message);
    }

    [Fact]
    public async Task RowWithTooManyFields_Throws()
    {
        var text = Header + "id1,Forest,1,,2026-09-21T10:00:00.0000000+00:00,42,Hash,art1,extra\r\n";

        await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));
    }

    [Fact]
    public async Task UnterminatedQuotedField_Throws()
    {
        var text = Header + "id1,\"Forest,1,,2026-09-21T10:00:00.0000000+00:00,42,Hash,art1\r\n";

        await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));
    }

    [Fact]
    public async Task NonIntegerQuantity_Throws()
    {
        var text = Header + "id1,Forest,not-a-number,,2026-09-21T10:00:00.0000000+00:00,42,Hash,art1\r\n";

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("Quantity", ex.Message);
    }

    [Fact]
    public async Task NonRoundtripTimestamp_Throws()
    {
        var text = Header + "id1,Forest,1,,not-a-date,42,Hash,art1\r\n";

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("LastScannedAt", ex.Message);
    }

    [Fact]
    public async Task UnknownSourceValue_Throws()
    {
        var text = Header + "id1,Forest,1,,2026-09-21T10:00:00.0000000+00:00,42,Robot,art1\r\n";

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("Source", ex.Message);
    }
}
