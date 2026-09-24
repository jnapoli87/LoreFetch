using System.Globalization;
using System.Text;

namespace LoreFetch.Lab.Accuracy;

/// One row of `test-images/ground-truth.csv`, package H3's fixed schema:
/// `file,height_in,layout,slot,oracle_name,rung,mat` (docs/history/orchestration-plan.md
/// "H3", ruling 2026-09-22: 7 columns, orientation deliberately NOT a
/// column -- it is invisible to identification by design and recoverable
/// from `File` for layout 9). `Slot` is 1-based, in the row-major order the
/// operator laid the physical grid out in (`capture-fixtures.sh`: "the
/// FIRST --card is slot 1 ... in slot order").
///
/// `File` is repo-relative to `test-images/` (e.g.
/// `fixtures/15in/9/15in-L9-light-normal-....png`), never an absolute path
/// -- matching what `capture-fixtures.sh` writes (`CSV_FILE_FIELD`).
public sealed record GroundTruthRow(
    string File,
    double HeightIn,
    int Layout,
    int Slot,
    string OracleName,
    string Rung,
    string Mat);

/// Thrown for anything that makes `ground-truth.csv` unusable: a header
/// that doesn't match the schema, a row with the wrong field count, an
/// unterminated quoted field, or a field that fails to parse into its typed
/// column. One exception type, mirroring `NativeCsvCodec`'s own
/// `NativeCsvFormatException` in stream D (not referenced here --
/// `Core/Collection` is stream D's own package and does not exist in this
/// worktree at all yet -- so this is a small, independent implementation of
/// the same RFC 4180 rules, not a copy of that type).
public sealed class GroundTruthCsvFormatException : Exception
{
    public GroundTruthCsvFormatException(string message)
        : base(message)
    {
    }

    public GroundTruthCsvFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// Reads (never writes -- `test-images/ground-truth.csv` is the operator's
/// own answer key, produced by `capture-fixtures.sh`, and this package must
/// treat it as read-only) the H3 ground-truth schema.
///
/// **Why a hand-rolled RFC 4180 tokenizer, not `string.Split(',')`:** B6's
/// brief measured 9.1% of oracle names containing a comma and therefore
/// quoted by `capture-fixtures.sh`'s own `csv_quote_field` (e.g.
/// `"Adéwalé, Breaker of Chains"`) -- a naive split corrupts roughly 1 row
/// in 11. The tokenizer below follows the same three RFC 4180 rules
/// `NativeCsvCodec`'s own doc comment states (quote on comma/quote/CR/LF,
/// double an embedded quote, never trim, and a field only opens a quoted
/// region when the FIRST character read for it is `"` -- whitespace before
/// an opening quote is content, not the start of a quoted field) --
/// deliberately re-derived here rather than copy-pasted, because
/// `Core/Collection` is stream D's own package and is not present in this
/// worktree to reference (stream D has not merged yet).
public static class GroundTruthCsvReader
{
    public static readonly IReadOnlyList<string> Columns =
        ["file", "height_in", "layout", "slot", "oracle_name", "rung", "mat"];

    public static IReadOnlyList<GroundTruthRow> Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    public static IReadOnlyList<GroundTruthRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var text = reader.ReadToEnd();
        var records = Tokenize(text);

        if (records.Count == 0)
        {
            throw new GroundTruthCsvFormatException("ground-truth.csv is empty; expected a header row.");
        }

        var columnIndex = ValidateHeader(records[0].Fields);

        var rows = new List<GroundTruthRow>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            rows.Add(ParseRow(records[i], columnIndex));
        }

        return rows;
    }

    /// Confirms the header's column SET matches exactly (order-independent,
    /// same reasoning as `NativeCsvCodec.ValidateHeader`: a hand-reordered
    /// file should still read, but an added/dropped/duplicated column is
    /// the format changing under us and must fail loudly rather than
    /// silently misassign a field).
    private static Dictionary<string, int> ValidateHeader(IReadOnlyList<string> headerFields)
    {
        var expected = new HashSet<string>(Columns, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < headerFields.Count; i++)
        {
            var name = headerFields[i];
            if (!seen.Add(name))
            {
                duplicates.Add(name);
                continue;
            }

            index[name] = i;
        }

        var unknown = seen.Where(n => !expected.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = expected.Where(n => !seen.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

        if (unknown.Count > 0 || missing.Count > 0 || duplicates.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
            {
                parts.Add($"missing: {string.Join(", ", missing)}");
            }

            if (unknown.Count > 0)
            {
                parts.Add($"unknown: {string.Join(", ", unknown)}");
            }

            if (duplicates.Count > 0)
            {
                parts.Add($"duplicated: {string.Join(", ", duplicates)}");
            }

            throw new GroundTruthCsvFormatException(
                $"ground-truth.csv header does not match the H3 schema ({string.Join("; ", parts)}). " +
                $"Expected exactly: {string.Join(",", Columns)}.");
        }

        return index;
    }

    private static GroundTruthRow ParseRow(Record record, Dictionary<string, int> columnIndex)
    {
        if (record.Fields.Count != Columns.Count)
        {
            throw new GroundTruthCsvFormatException(
                $"Line {record.LineNumber}: expected {Columns.Count} columns, found {record.Fields.Count}.");
        }

        string Field(string column) => record.Fields[columnIndex[column]];

        var file = Field("file");
        var heightIn = ParseTyped(record.LineNumber, "height_in", Field("height_in"),
            s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
        var layout = ParseTyped(record.LineNumber, "layout", Field("layout"),
            s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
        var slot = ParseTyped(record.LineNumber, "slot", Field("slot"),
            s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
        var oracleName = Field("oracle_name");
        var rung = Field("rung");
        var mat = Field("mat");

        return new GroundTruthRow(file, heightIn, layout, slot, oracleName, rung, mat);
    }

    private static T ParseTyped<T>(int lineNumber, string column, string raw, Func<string, T> parse)
    {
        try
        {
            return parse(raw);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new GroundTruthCsvFormatException(
                $"Line {lineNumber}: could not parse column '{column}' value '{raw}'.", ex);
        }
    }

    private readonly record struct Record(int LineNumber, List<string> Fields);

    /// Same tokenizer shape as `NativeCsvCodec.Tokenize` (see that type's own
    /// doc comment for the full RFC 4180 rationale) -- whole-text, not
    /// line-by-line, because a quoted field may legally contain a CR or LF.
    private static List<Record> Tokenize(string text)
    {
        var records = new List<Record>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var lineNumber = 1;
        var recordStartLine = 1;
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    lineNumber++;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    i++;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;

                case '\r':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(new Record(recordStartLine, fields));
                    fields = [];
                    i++;
                    lineNumber++;
                    if (i < n && text[i] == '\n')
                    {
                        i++;
                    }

                    recordStartLine = lineNumber;
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(new Record(recordStartLine, fields));
                    fields = [];
                    i++;
                    lineNumber++;
                    recordStartLine = lineNumber;
                    break;

                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (inQuotes)
        {
            throw new GroundTruthCsvFormatException($"Line {recordStartLine}: unterminated quoted field.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new Record(recordStartLine, fields));
        }

        return records;
    }
}
