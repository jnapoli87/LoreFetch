using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// B6's brief: "9.1% of oracle names contain a comma and are therefore
/// quoted... a naive split(',') will corrupt ~1 row in 11." These tests pin
/// the RFC 4180 tokenizer directly against that exact failure mode, plus
/// the header/format-integrity checks `capture-fixtures.sh`'s own writer
/// depends on staying strict.
public class GroundTruthCsvReaderTests
{
    private const string Header = "file,height_in,layout,slot,oracle_name,rung,mat";

    [Fact]
    public void Read_QuotedOracleNameWithEmbeddedComma_ParsesAsOneField()
    {
        var csv = Header + "\r\n" +
                  "fixtures/15in/1/x.png,15,1,1,\"Adéwalé, Breaker of Chains\",normal,light\r\n";

        var rows = GroundTruthCsvReader.Read(new StringReader(csv));

        Assert.Single(rows);
        Assert.Equal("Adéwalé, Breaker of Chains", rows[0].OracleName);
        Assert.Equal(15.0, rows[0].HeightIn);
        Assert.Equal(1, rows[0].Layout);
        Assert.Equal(1, rows[0].Slot);
        Assert.Equal("normal", rows[0].Rung);
        Assert.Equal("light", rows[0].Mat);
    }

    /// Chaos-test companion: a naive `line.Split(',')` over the SAME row
    /// would read the comma inside the quotes as a field separator and
    /// produce 8 fields instead of 7, corrupting `rung`/`mat` -- this test
    /// exists specifically because that failure is silent (wrong values,
    /// not a thrown exception) unless something asserts the exact parsed
    /// values, which is exactly what the assertions above do.
    [Fact]
    public void Read_QuotedOracleNameWithEmbeddedComma_WouldCorruptUnderNaiveSplit()
    {
        var line = "fixtures/15in/1/x.png,15,1,1,\"Adéwalé, Breaker of Chains\",normal,light";
        var naiveFields = line.Split(',');

        // The naive split produces 8 fields (the embedded comma splits the
        // quoted name in two), NOT the 7 the real schema has -- confirming
        // this row is a genuine trap for a naive parser, not a hypothetical
        // one.
        Assert.Equal(8, naiveFields.Length);
    }

    [Fact]
    public void Read_MultipleRowsIncludingUnquotedNames_ParsesAll()
    {
        var csv = Header + "\r\n" +
                  "fixtures/15in/3/a.png,15,3,1,Forest,land,light\r\n" +
                  "fixtures/15in/3/a.png,15,3,2,\"Lim-Dûl's Vault\",normal,light\r\n" +
                  "fixtures/15in/3/a.png,15,3,3,Counterspell,normal,light\r\n";

        var rows = GroundTruthCsvReader.Read(new StringReader(csv));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Forest", rows[0].OracleName);
        Assert.Equal("Lim-Dûl's Vault", rows[1].OracleName);
        Assert.Equal("Counterspell", rows[2].OracleName);
    }

    [Fact]
    public void Read_MissingColumn_ThrowsNamingWhatIsMissing()
    {
        var csv = "file,height_in,layout,slot,oracle_name,rung\r\n" + // "mat" missing
                  "fixtures/15in/1/x.png,15,1,1,Forest,land\r\n";

        var ex = Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthCsvReader.Read(new StringReader(csv)));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_UnknownExtraColumn_ThrowsNamingIt()
    {
        var csv = Header + ",orientation\r\n" +
                  "fixtures/15in/1/x.png,15,1,1,Forest,land,light,portrait\r\n";

        var ex = Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthCsvReader.Read(new StringReader(csv)));
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orientation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ReorderedHeader_StillParsesByName()
    {
        var reordered = "mat,file,rung,height_in,layout,slot,oracle_name";
        var csv = reordered + "\r\n" +
                  "light,fixtures/15in/1/x.png,normal,15,1,1,Sol Ring\r\n";

        var rows = GroundTruthCsvReader.Read(new StringReader(csv));

        Assert.Single(rows);
        Assert.Equal("Sol Ring", rows[0].OracleName);
        Assert.Equal("fixtures/15in/1/x.png", rows[0].File);
        Assert.Equal("light", rows[0].Mat);
    }

    [Fact]
    public void Read_UnterminatedQuote_Throws()
    {
        var csv = Header + "\r\n" +
                  "fixtures/15in/1/x.png,15,1,1,\"Forest,land,light\r\n";

        Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthCsvReader.Read(new StringReader(csv)));
    }

    [Fact]
    public void Read_WrongFieldCount_ThrowsWithLineNumber()
    {
        var csv = Header + "\r\n" +
                  "fixtures/15in/1/x.png,15,1,1,Forest,land\r\n"; // only 6 fields, row 2

        var ex = Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthCsvReader.Read(new StringReader(csv)));
        Assert.Contains("Line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_EmptyFile_Throws()
    {
        Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthCsvReader.Read(new StringReader(string.Empty)));
    }
}
