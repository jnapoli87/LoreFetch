using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.Collection;

/// The header's exact column SET is the format version (DECISIONS.md
/// §Storage): a reader seeing an unknown or missing column must fail
/// loudly rather than mis-parse.
public class NativeCsvCodecHeaderTests
{
    [Fact]
    public async Task MissingColumn_Throws()
    {
        var text = "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source\r\n"; // ArtworkId dropped

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("ArtworkId", ex.Message);
    }

    [Fact]
    public async Task UnknownColumn_Throws()
    {
        var text = "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId,Foo\r\n";

        var ex = await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));

        Assert.Contains("Foo", ex.Message);
    }

    [Fact]
    public async Task DuplicateColumn_Throws()
    {
        var text = "OracleId,OracleName,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId\r\n";

        await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyFile_Throws()
    {
        await Assert.ThrowsAsync<NativeCsvFormatException>(
            () => NativeCsvCodec.ReadAsync(new StringReader(string.Empty), logger: null, CancellationToken.None));
    }

    [Fact]
    public async Task ReorderedButCompleteHeader_StillParses()
    {
        // Column SET is the version, not order — CollectionRow's own doc
        // comment calls order "incidental". A hand-reordered file (plausible
        // after an Excel save) must still read.
        var oracleId = TestSupport.Row().OracleId;
        var text =
            "ArtworkId,OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source\r\n" +
            $",{oracleId},Forest,1,,{TestSupport.SampleTimestamp:o},42,Hash\r\n";

        var rows = await NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Forest", row.OracleName);
        Assert.Null(row.ArtworkId);
    }
}
