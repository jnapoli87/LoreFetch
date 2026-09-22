using System.Text;
using LoreFetch.Core.Export;
using Xunit;

namespace LoreFetch.Tests.StreamD;

/// <summary>
/// Tests for <see cref="MoxfieldCsvExporter"/>. All assertions work on
/// in-memory streams — no file I/O, no process-external state.
/// </summary>
public class MoxfieldCsvExporterTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static MoxfieldCsvExporter NewExporter() => new();

    private static async Task<string[]> ExportLinesAsync(
        IReadOnlyList<LoreFetch.Core.Abstractions.CollectionRow> rows)
    {
        using var stream = new MemoryStream();
        await NewExporter().ExportAsync(rows, stream, CancellationToken.None);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        // Split on CRLF; the exporter uses RFC 4180 terminators.
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    // ---------------------------------------------------------------
    // Format metadata
    // ---------------------------------------------------------------

    [Fact]
    public void Format_HasExpectedMetadata()
    {
        var exporter = NewExporter();

        Assert.Equal("moxfield", exporter.Format.Id);
        Assert.Equal("csv", exporter.Format.FileExtension);
        Assert.True(exporter.Format.IsVerified,
            "IsVerified was flipped to true on 2026-09-22 (D5) after a real 6-card import into a live Moxfield account landed correctly.");
        Assert.False(string.IsNullOrWhiteSpace(exporter.Format.DisplayName));
        // Notes must mention both the printing-columns-blank and the
        // unverified-import limitations so the UI can surface them.
        Assert.NotNull(exporter.Format.Notes);
        Assert.Contains("blank", exporter.Format.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verified", exporter.Format.Notes, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_FirstLine_IsExactMoxfieldHeader()
    {
        // Header spelling and case MUST match exactly — Moxfield's own help
        // page says so explicitly. Column ORDER is explicitly irrelevant per
        // the spec, but we pin it for reproducibility.
        // Source: https://moxfield.com/help/help-articles/importing-collection
        var lines = await ExportLinesAsync([TestSupport.Row()]);

        Assert.Equal(
            "Count,Name,Edition,Condition,Language,Foil,Collector Number,Alter,Playtest Card,Purchase Price",
            lines[0]);
    }

    // ---------------------------------------------------------------
    // Empty collection
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_EmptyCollection_ProducesHeaderOnlyFile_NotZeroBytes()
    {
        // A zero-byte file would be rejected by most importers with an
        // unhelpful error; a header-only file is a valid, parseable CSV.
        var lines = await ExportLinesAsync([]);

        var single = Assert.Single(lines);
        Assert.Equal(
            "Count,Name,Edition,Condition,Language,Foil,Collector Number,Alter,Playtest Card,Purchase Price",
            single);
    }

    // ---------------------------------------------------------------
    // No BOM
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_OutputHasNoBom()
    {
        // Moxfield's importer demands exact header matching and warns about
        // stray characters; a UTF-8 BOM prepended to "Count" is a plausible
        // break. The first bytes must be 'C', 'o', 'u', 'n', 't' — not the
        // three-byte UTF-8 BOM sequence EF BB BF.
        using var stream = new MemoryStream();
        await NewExporter().ExportAsync([TestSupport.Row()], stream, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length >= 5, "Output is unexpectedly short.");
        Assert.Equal((byte)'C', bytes[0]);
        Assert.Equal((byte)'o', bytes[1]);
        Assert.Equal((byte)'u', bytes[2]);
        Assert.Equal((byte)'n', bytes[3]);
        Assert.Equal((byte)'t', bytes[4]);
    }

    // ---------------------------------------------------------------
    // leaveOpen — caller's stream stays usable
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_LeavesTheCallersStreamOpenAndUsable()
    {
        // CONTRACTS.md mechanic 1: ExportAsync must not dispose the caller's
        // stream — the caller owns it and may write a second thing after.
        using var stream = new MemoryStream();

        await NewExporter().ExportAsync([TestSupport.Row()], stream, CancellationToken.None);

        Assert.True(stream.CanWrite, "Exporter must not dispose the destination stream.");
        Assert.True(stream.CanSeek, "Exporter must not dispose the destination stream.");
    }

    // ---------------------------------------------------------------
    // Count and Name only — every other column blank
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_DataRow_PopulatesOnlyCountAndName_AllOtherColumnsBlank()
    {
        // Eight other columns must be blank — never the literal "null",
        // never a fabricated set code or condition.
        var row = TestSupport.Row(
            oracleId: "aaaa",
            oracleName: "Forest",
            quantity: 3,
            condition: null,          // always null in v1
            bestMatchDistance: 42,
            source: LoreFetch.Core.Abstractions.RowSource.Hash,
            artworkId: "bbbb");

        var lines = await ExportLinesAsync([row]);

        // lines[0] is the header; lines[1] is the data row.
        Assert.Equal(2, lines.Length);
        // Count=3, Name=Forest, then 8 blank fields.
        Assert.Equal("3,Forest,,,,,,,,", lines[1]);
    }

    [Fact]
    public async Task ExportAsync_NeverWritesNullLiteralInAnyColumn()
    {
        // Belt-and-suspenders: no column value in any row should be the
        // string "null" — the field is either populated or blank.
        var row = TestSupport.Row(
            condition: null,
            bestMatchDistance: null,
            artworkId: null);

        using var stream = new MemoryStream();
        await NewExporter().ExportAsync([row], stream, CancellationToken.None);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // Escaping vectors — all five required by docs/stream-d-export.md §D3
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_KongmingSleeingDragon_QuotesCommaAndEmbeddedQuotes()
    {
        // "Kongming, \"Sleeping Dragon\"" — comma AND embedded double quotes.
        // A naive writer that quotes only on comma would still miss the
        // doubling of the embedded quotes; a correctly quoted field wraps
        // the whole value and doubles the internal quotes.
        const string oracleName = "Kongming, \"Sleeping Dragon\"";
        var lines = await ExportLinesAsync([TestSupport.Row(oracleName: oracleName)]);

        // Expected: 1,"Kongming, ""Sleeping Dragon""",,,,,,,,
        Assert.Equal(2, lines.Length);
        var dataLine = lines[1];
        Assert.StartsWith("1,\"Kongming, \"\"Sleeping Dragon\"\"\"", dataLine);
    }

    [Fact]
    public async Task ExportAsync_RumorsOfMyDeath_QuotesLeadingQuoteWithNoComma()
    {
        // "\"Rumors of My Death . . .\"" — leading double quote, no comma at all.
        // A writer that quotes only when it sees a comma emits this name bare,
        // and a conforming RFC 4180 reader then interprets the leading " as
        // the start of a quoted field and mis-reads the row. This is the
        // worst-case vector from docs/stream-d-export.md §D2.
        const string oracleName = "\"Rumors of My Death . . .\"";
        var lines = await ExportLinesAsync([TestSupport.Row(oracleName: oracleName)]);

        Assert.Equal(2, lines.Length);
        var dataLine = lines[1];
        // The name must be quoted (first char after "1," must be '"').
        var afterCount = dataLine.Substring("1,".Length);
        Assert.StartsWith("\"", afterCount);
        // And the embedded quotes must be doubled.
        Assert.Contains("\"\"Rumors of My Death . . .\"\"", dataLine);
    }

    [Fact]
    public async Task ExportAsync_LimDulsVault_NonAsciiAndApostrophe_RoundTrips()
    {
        // "Lim-Dûl's Vault" — non-ASCII (û) and an apostrophe.
        // Verifies the adapter writes UTF-8 correctly and doesn't corrupt
        // non-ASCII characters during encoding. Verified as a real oracle
        // name against Scryfall on 2026-09-21.
        const string oracleName = "Lim-Dûl's Vault";
        var lines = await ExportLinesAsync([TestSupport.Row(oracleName: oracleName)]);

        Assert.Equal(2, lines.Length);
        // Name contains no comma or quote, so it is not wrapped.
        Assert.StartsWith("1,Lim-Dûl's Vault", lines[1]);
    }

    [Fact]
    public async Task ExportAsync_Borrowing100000Arrows_CommaInNumber()
    {
        // "Borrowing 100,000 Arrows" — comma inside a number.
        // Also tempts a locale-aware number formatter. The comma must trigger
        // quoting, and the value must survive verbatim.
        const string oracleName = "Borrowing 100,000 Arrows";
        var lines = await ExportLinesAsync([TestSupport.Row(oracleName: oracleName)]);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1,\"Borrowing 100,000 Arrows\"", lines[1]);
    }

    [Fact]
    public async Task ExportAsync_Plus2Mace_NoSanitisationPrefix()
    {
        // "+2 Mace" — leading '+', the Excel formula-injection case.
        // The adapter MUST NOT prefix any sanitisation character ('  or tab)
        // because doing so would corrupt the source of truth for every machine
        // reader (CLAUDE.md: "never sanitise"). The value must appear verbatim.
        const string oracleName = "+2 Mace";
        var lines = await ExportLinesAsync([TestSupport.Row(oracleName: oracleName)]);

        Assert.Equal(2, lines.Length);
        // "+2 Mace" contains no comma/quote/CR/LF — it should appear unquoted.
        Assert.Equal("1,+2 Mace,,,,,,,,", lines[1]);
    }

    // ---------------------------------------------------------------
    // Multiple rows
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExportAsync_MultipleRows_EachRowHasCorrectFieldCount()
    {
        // Regression guard: every data row must have exactly 10 fields (the
        // header column count) regardless of the card name's content.
        var rows = new[]
        {
            TestSupport.Row(oracleName: "Forest", quantity: 9),
            TestSupport.Row(oracleName: "Kongming, \"Sleeping Dragon\"", quantity: 1),
            TestSupport.Row(oracleName: "+2 Mace", quantity: 2),
        };

        var lines = await ExportLinesAsync(rows);

        // Header + 3 data rows.
        Assert.Equal(4, lines.Length);
        foreach (var dataLine in lines.Skip(1))
        {
            // Count the commas OUTSIDE of quoted regions to get the field count.
            var fieldCount = CountFields(dataLine);
            Assert.Equal(10, fieldCount);
        }
    }

    /// Counts comma-delimited fields in a single RFC 4180 row by walking
    /// the string and tracking quoted regions — not a general tokenizer, but
    /// sufficient for single-line assertions where we know there is no
    /// embedded newline.
    private static int CountFields(string line)
    {
        var count = 1;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
