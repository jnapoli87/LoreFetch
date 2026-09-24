using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Xunit;

namespace LoreFetch.Tests.Collection;

public class NativeCsvCodecWriteTests
{
    [Fact]
    public async Task EmptyCollection_ProducesHeaderOnlyFile()
    {
        var writer = new StringWriter();

        await NativeCsvCodec.WriteAsync(writer, [], CancellationToken.None);

        Assert.Equal(
            "OracleId,OracleName,Quantity,Condition,LastScannedAt,BestMatchDistance,Source,ArtworkId\r\n",
            writer.ToString());
    }

    [Fact]
    public async Task RowWithCommaAndEmbeddedQuotes_IsQuoted()
    {
        // "Kongming, "Sleeping Dragon"" — comma AND an embedded quote in one field.
        var row = TestSupport.Row(oracleName: "Kongming, \"Sleeping Dragon\"");
        var writer = new StringWriter();

        await NativeCsvCodec.WriteAsync(writer, [row], CancellationToken.None);

        var line = SecondLine(writer.ToString());
        Assert.Contains("\"Kongming, \"\"Sleeping Dragon\"\"\"", line);
    }

    [Fact]
    public async Task PlusPrefixedName_IsWrittenVerbatimNeverSanitised()
    {
        // "+2 Mace" — the only oracle name Excel treats as a formula. The
        // native format's job is correctness for machine readers; sanitising
        // it here would corrupt the source of truth to fix one program's
        // display. See DECISIONS.md's Excel-formula-injection note.
        var row = TestSupport.Row(oracleName: "+2 Mace");
        var writer = new StringWriter();

        await NativeCsvCodec.WriteAsync(writer, [row], CancellationToken.None);

        var line = SecondLine(writer.ToString());
        var fields = line.Split(',');
        Assert.Equal("+2 Mace", fields[1]);
    }

    [Fact]
    public async Task FieldWithEmbeddedCarriageReturnOnly_IsQuoted()
    {
        // No real oracle name contains a bare CR, but the quoting rule must
        // not depend on LF also being present — RFC 4180 requires quoting on
        // CR *or* LF independently. See NativeCsvCodec.QuoteField's doc
        // comment and chaos test 3 in the work package.
        var row = TestSupport.Row(oracleName: "Two\rLines");
        var writer = new StringWriter();

        await NativeCsvCodec.WriteAsync(writer, [row], CancellationToken.None);

        var text = writer.ToString();
        Assert.Contains("\"Two\rLines\"", text);
    }

    [Fact]
    public async Task BlankCondition_IsWrittenAsEmptyFieldNeverTheLiteralNull()
    {
        var row = TestSupport.Row(condition: null);
        var writer = new StringWriter();

        await NativeCsvCodec.WriteAsync(writer, [row], CancellationToken.None);

        var fields = SecondLine(writer.ToString()).Split(',');
        Assert.Equal(string.Empty, fields[3]);
    }

    [Fact]
    public async Task Stream_WriteAsync_EmitsUtf8Bom()
    {
        using var stream = new MemoryStream();

        await NativeCsvCodec.WriteAsync(stream, [TestSupport.Row()], CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public async Task Stream_WriteAsync_DoesNotDisposeCallersStream()
    {
        using var stream = new MemoryStream();

        await NativeCsvCodec.WriteAsync(stream, [TestSupport.Row()], CancellationToken.None);

        // If the codec had disposed the stream (StreamWriter's default
        // behaviour without leaveOpen: true), this would throw
        // ObjectDisposedException — the exact bug CONTRACTS.md's mechanic 1
        // calls out.
        Assert.True(stream.CanWrite);
    }

    private static string SecondLine(string text) => text.Split("\r\n")[1];
}
