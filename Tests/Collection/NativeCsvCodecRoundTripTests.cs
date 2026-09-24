using System.Text;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.Collection;

/// The five required vectors from docs/design/collection.md §D3, plus one
/// implementation-level regression vector (embedded CR) not in that table
/// because no real oracle name needs it — see NativeCsvCodecWriteTests'
/// matching case. All verified as real oracle names against Scryfall on
/// 2026-09-21 per the doc.
public class NativeCsvCodecRoundTripTests
{
    public static IEnumerable<object[]> Vectors =>
    [
        ["Kongming, \"Sleeping Dragon\""],   // comma AND embedded quotes in one field
        ["\"Rumors of My Death . . .\""],    // leading quote, NO comma — a quote-only-if-comma writer emits this bare
        ["Lim-Dûl's Vault"],            // non-ASCII plus an apostrophe
        ["Borrowing 100,000 Arrows"],        // comma inside a number
        ["+2 Mace"],                         // leading '+', Excel formula character
    ];

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Vector_RoundTripsThroughTextWriterAndReader(string oracleName)
    {
        var original = TestSupport.Row(oracleName: oracleName);
        var writer = new StringWriter();
        await NativeCsvCodec.WriteAsync(writer, [original], CancellationToken.None);

        var rows = await NativeCsvCodec.ReadAsync(new StringReader(writer.ToString()), logger: null, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(original, row);
    }

    [Fact]
    public async Task LeadingWhitespaceBeforeQuote_ParsesAsLiteralContentNotAQuotedField()
    {
        // The override this codec was built against: a field that starts
        // with whitespace and THEN a quote is not a quoted field — the
        // whitespace is content, so the quote that follows is just another
        // character. `Microsoft.VisualBasic.FileIO.TextFieldParser` gets
        // this wrong (docs/design/collection.md §D1); this codec must not.
        var oracleId = TestSupport.Row().OracleId;
        var text =
            "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId\r\n" +
            $"{oracleId}, \"abc\",1,,{TestSupport.SampleTimestamp:o},42,Hash,\r\n";

        var rows = await NativeCsvCodec.ReadAsync(new StringReader(text), logger: null, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(" \"abc\"", row.OracleName);
    }

    [Fact]
    public async Task EmptyCollection_RoundTripsToZeroRows()
    {
        var writer = new StringWriter();
        await NativeCsvCodec.WriteAsync(writer, [], CancellationToken.None);

        var rows = await NativeCsvCodec.ReadAsync(new StringReader(writer.ToString()), logger: null, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Stream_RoundTrip_WithBomIsNotReadAsData()
    {
        using var stream = new MemoryStream();
        var original = TestSupport.Row(oracleName: "Forest");
        await NativeCsvCodec.WriteAsync(stream, [original], CancellationToken.None);
        stream.Position = 0;

        var rows = await NativeCsvCodec.ReadAsync(stream, logger: null, CancellationToken.None);

        // If the BOM had been read as data, the header check would have
        // failed with "unknown column" (U+FEFF prepended to "OracleId")
        // rather than this succeeding at all.
        var row = Assert.Single(rows);
        Assert.Equal(original, row);
    }

    [Fact]
    public async Task Stream_RoundTrip_WithoutBomAlsoParses()
    {
        var text =
            "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId\r\n" +
            $"{TestSupport.Row().OracleId},Forest,1,,{TestSupport.SampleTimestamp:o},42,Hash,\r\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); // no preamble

        var rows = await NativeCsvCodec.ReadAsync(stream, logger: null, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Forest", row.OracleName);
    }
}
