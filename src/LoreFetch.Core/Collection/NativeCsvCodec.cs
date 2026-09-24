using System.Globalization;
using System.Text;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Core.Collection;

/// <summary>
/// Reader and writer for the native CSV format — the source of truth
/// described in CLAUDE.md §Storage and docs/CONTRACTS.md §"Collection and
/// export". This is the codec only: quoting, header validation, field
/// parsing and intra-file duplicate folding. It knows nothing about files,
/// temp-file-then-rename, or <c>.bak</c> copies — that is the collection
/// store's job (built on top of this), and the native
/// <c>ICollectionExporter</c> is the other caller. Both share exactly this
/// code so the most-used path in the project has one quoting
/// implementation and one header check, not two that can drift apart.
///
/// <para>
/// <b>Hand-rolled rather than CsvHelper.</b> <c>CsvHelper</c> stays
/// referenced (Directory.Packages.props) as the pre-fork insurance the
/// plan review called for, but this codec does not use it, for two
/// reasons that hold regardless of which library is picked:
/// </para>
/// <list type="number">
/// <item>No CSV library enforces the half of the format-version guard that
/// actually matters here. CsvHelper throws on a *missing* expected header
/// by default, but silently ignores an *unknown, extra* one — there is no
/// configuration flag that changes that — so "fail loudly on an unknown
/// column" is hand-written code either way (docs/design/collection.md §D1,
/// point 4).</item>
/// <item>Writing RFC 4180 is four rules (quote on comma/quote/CR/LF, double
/// an embedded quote, never trim, quote-only-if-comma is wrong). A
/// dependency does not remove any of that work, it just moves it behind an
/// API this project would still need to wrap to get the unknown-column
/// check.</item>
/// </list>
/// </summary>
public static class NativeCsvCodec
{
    /// <summary>
    /// The column set, in the order this codec writes them. Order is
    /// incidental — <see cref="ReadAsync(TextReader,ILogger?,CancellationToken)"/>
    /// resolves columns by name so a hand-reordered file still reads — but the
    /// SET is the format version: see <see cref="ValidateHeader"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "OracleId",
        "OracleName",
        "Quantity",
        "Condition",
        "LastScannedAt",
        "BestMatchDistance",
        "Source",
        "ArtworkId",
    ];

    private static readonly string HeaderLine = string.Join(',', Columns);

    // RFC 4180 line terminator, written explicitly rather than
    // Environment.NewLine so the byte-for-byte output — and every test
    // against it — is identical on Windows and macOS CI.
    private const string RecordTerminator = "\r\n";

    // ---------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes the header followed by one line per row. An empty
    /// <paramref name="rows"/> list still produces the header line — a
    /// valid, parseable file describing zero cards, never an empty file a
    /// reader would have to special-case.
    /// </summary>
    public static async Task WriteAsync(TextWriter writer, IReadOnlyList<CollectionRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);

        await writer.WriteAsync(HeaderLine).ConfigureAwait(false);
        await writer.WriteAsync(RecordTerminator).ConfigureAwait(false);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteAsync(FormatRow(row)).ConfigureAwait(false);
            await writer.WriteAsync(RecordTerminator).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stream convenience over <see cref="WriteAsync(TextWriter,IReadOnlyList{CollectionRow},CancellationToken)"/>.
    /// Uses UTF-8 <b>with</b> BOM — the native format's own rule, because it
    /// is the file a user opens directly in Excel, which mangles non-ASCII
    /// card names without one. The BOM only lands if <paramref name="destination"/>
    /// is at position 0 at first flush (StreamWriter's own rule — see
    /// docs/design/collection.md §D1, mechanic 2); a fresh temp file
    /// satisfies that, an append would silently drop it. Does not close or
    /// dispose <paramref name="destination"/> — the caller owns it (the
    /// same <c>leaveOpen</c> requirement CONTRACTS.md places on every
    /// <c>ICollectionExporter</c>).
    /// </summary>
    public static async Task WriteAsync(Stream destination, IReadOnlyList<CollectionRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        // encoderShouldEmitUTF8Identifier: true → the BOM. throwOnInvalidBytes:
        // true → fail loudly on an unencodable sequence rather than silently
        // substituting '?', matching the "never sanitise a name" rule below.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        var writer = new StreamWriter(destination, encoding, bufferSize: 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await WriteAsync(writer, rows, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static string FormatRow(CollectionRow row)
    {
        // Oracle names are written verbatim, never sanitised: `+2 Mace` is a
        // real in-scope card and the only oracle name Excel treats as a
        // formula. A `'`/tab-prefix mitigation would corrupt the source of
        // truth for every machine reader to fix one program's rendering —
        // CLAUDE.md and CONTRACTS.md are both explicit that this is a
        // README note, not a code path.
        string[] fields =
        [
            row.OracleId,
            row.OracleName,
            row.Quantity.ToString(CultureInfo.InvariantCulture),
            row.Condition ?? string.Empty,
            row.LastScannedAt.ToString("o", CultureInfo.InvariantCulture),
            row.BestMatchDistance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.Source.ToString(),
            row.ArtworkId ?? string.Empty,
        ];

        return string.Join(',', fields.Select(QuoteField));
    }

    /// <summary>
    /// RFC 4180 §2: quote a field that contains a comma, a double quote, or
    /// a CR/LF, and escape an embedded quote by doubling it. Spaces are
    /// field content and are never trimmed. Note this checks for CR and LF
    /// independently — quoting only on LF (skipping bare CR, or a CR that
    /// isn't immediately followed by LF) would let a field containing a
    /// lone CR through unquoted, and the reader below treats an unquoted
    /// CR as content, not a record terminator, so a naive reader downstream
    /// would not — the two behaviours must agree, or a value with an
    /// embedded CR corrupts the row after it.
    /// <para>
    /// Exposed <c>internal</c> so third-party adapters (e.g.
    /// <c>MoxfieldCsvExporter</c>) can reuse the same quoting rather than
    /// reimplementing RFC 4180.
    /// </para>
    /// </summary>
    internal static string QuoteField(string field)
    {
        if (field.IndexOfAny(['\r', '\n', '"', ',']) < 0)
        {
            return field;
        }

        return string.Concat('"', field.Replace("\"", "\"\""), '"');
    }

    // ---------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------

    /// <summary>
    /// Parses the native format, validates the header, folds duplicate
    /// <c>OracleId</c> + <c>Condition</c> rows (logging each fold), and
    /// returns the resulting rows. Fails loudly — <see cref="NativeCsvFormatException"/> —
    /// on an unknown/missing header column, a row with the wrong field
    /// count, an unterminated quoted field, or a field that does not parse
    /// into its typed member. <paramref name="logger"/> may be null; a
    /// missing logger only means duplicate merges go unrecorded, never that
    /// they silently stop being merged.
    /// </summary>
    public static async Task<IReadOnlyList<CollectionRow>> ReadAsync(TextReader reader, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ct.ThrowIfCancellationRequested();

        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var records = Tokenize(text);

        if (records.Count == 0)
        {
            throw new NativeCsvFormatException("The file is empty; expected a header row.");
        }

        var header = records[0];
        var columnIndex = ValidateHeader(header.Fields);

        var rows = new List<CollectionRow>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(ParseRow(records[i], columnIndex));
        }

        return MergeDuplicates(rows, logger);
    }

    /// <summary>
    /// Stream convenience over <see cref="ReadAsync(TextReader,ILogger?,CancellationToken)"/>.
    /// Detects and strips a leading UTF-8 BOM, and reads correctly when
    /// there is none — <c>detectEncodingFromByteOrderMarks: true</c> makes
    /// both true regardless of which default encoding is supplied, so the
    /// same call handles a file this codec wrote (BOM present) and one a
    /// user hand-saved without one. Getting this wrong turns the BOM into
    /// data — <c>"﻿OracleId" != "OracleId"</c> — which fails the
    /// header check in the worst way: it looks like a corrupt file rather
    /// than an encoding bug. Does not close or dispose <paramref name="source"/>.
    /// </summary>
    public static async Task<IReadOnlyList<CollectionRow>> ReadAsync(Stream source, ILogger? logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        using var reader = new StreamReader(source, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return await ReadAsync(reader, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirms the header's column SET matches exactly — no unknown
    /// column, none missing, none duplicated — and returns a name→position
    /// map for the rows that follow. This is deliberately a set comparison,
    /// not a positional one: the column set IS the format version
    /// (CLAUDE.md §Storage), not the column order, which
    /// <see cref="Abstractions.CollectionRow"/>'s own doc comment calls
    /// "incidental". A hand-reordered file — plausible after an Excel
    /// save — still reads; an added or dropped column does not.
    /// </summary>
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

        var unknown = seen.Where(name => !expected.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = expected.Where(name => !seen.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();

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

            throw new NativeCsvFormatException(
                $"Header does not match the native format's column set ({string.Join("; ", parts)}). " +
                "The header's exact column set is the format version; see CLAUDE.md §Storage.");
        }

        return index;
    }

    private static CollectionRow ParseRow(Record record, Dictionary<string, int> columnIndex)
    {
        if (record.Fields.Count != Columns.Count)
        {
            throw new NativeCsvFormatException(
                $"Line {record.LineNumber}: expected {Columns.Count} columns, found {record.Fields.Count}.");
        }

        string Field(string column) => record.Fields[columnIndex[column]];

        var oracleId = Field("OracleId");
        var oracleName = Field("OracleName");
        var quantity = ParseTypedField(record.LineNumber, "Quantity", Field("Quantity"),
            s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));

        // Blank is the ONLY on-disk representation of "not assessed", and it
        // parses back to null, never "". A quoted-but-empty field ("") and
        // an unquoted-empty field are indistinguishable once the surrounding
        // quotes are stripped — deliberately: there is no second way to
        // write "unassessed" that this codec preserves, so there is no
        // separate empty-string state to round-trip. See CollectionRow's own
        // doc comment and CLAUDE.md §Storage.
        var conditionRaw = Field("Condition");
        var condition = conditionRaw.Length == 0 ? null : conditionRaw;

        var lastScannedAt = ParseTypedField(record.LineNumber, "LastScannedAt", Field("LastScannedAt"),
            s => DateTimeOffset.ParseExact(s, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        var bestMatchRaw = Field("BestMatchDistance");
        var bestMatchDistance = bestMatchRaw.Length == 0
            ? (int?)null
            : ParseTypedField(record.LineNumber, "BestMatchDistance", bestMatchRaw,
                s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));

        var source = ParseTypedField(record.LineNumber, "Source", Field("Source"),
            s => Enum.Parse<RowSource>(s));

        var artworkRaw = Field("ArtworkId");
        var artworkId = artworkRaw.Length == 0 ? null : artworkRaw;

        return new CollectionRow(oracleId, oracleName, quantity, condition, lastScannedAt, bestMatchDistance, source, artworkId);
    }

    private static T ParseTypedField<T>(int lineNumber, string column, string raw, Func<string, T> parse)
    {
        try
        {
            return parse(raw);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new NativeCsvFormatException(
                $"Line {lineNumber}: could not parse column '{column}' value '{raw}'.", ex);
        }
    }

    /// <summary>
    /// Folds rows sharing an <c>OracleId</c> + <c>Condition</c> key —
    /// import robustness (CONTRACTS.md §"Collection and export"): a file
    /// this codec did not write itself (hand-edited, pasted, re-sorted)
    /// can carry the same card twice, and the reader must heal that rather
    /// than lock a user out of their own collection. This is a deliberate
    /// exception to fail-loudly, which governs columns, not keys — a
    /// duplicate key has one unambiguous correct resolution and a shifted
    /// column does not.
    ///
    /// Quantity sums. <c>ArtworkId</c> uses <see cref="FoldArtworkId"/> —
    /// agree-or-null across every merged row, never last-write-wins,
    /// because a wrong printing id reads as confidently right. Everything
    /// else (<c>OracleName</c>, <c>LastScannedAt</c>, <c>BestMatchDistance</c>,
    /// <c>Source</c>) is undocumented for this specific case in
    /// CONTRACTS.md beyond "keep the latest LastScannedAt", so this codec
    /// takes the whole row with the latest <c>LastScannedAt</c> as the
    /// basis and only overrides Quantity and ArtworkId on it: the freshest
    /// scan's distance/source is more likely still accurate than an older
    /// one's, and pairing an old distance with a new timestamp would be its
    /// own kind of misleading.
    /// </summary>
    private static IReadOnlyList<CollectionRow> MergeDuplicates(List<CollectionRow> rows, ILogger? logger)
    {
        var groups = rows
            .Select((row, position) => (row, position))
            .GroupBy(x => (x.row.OracleId, x.row.Condition));

        var result = new List<CollectionRow>(rows.Count);

        foreach (var group in groups)
        {
            var members = group.OrderBy(x => x.row.LastScannedAt).ThenBy(x => x.position).Select(x => x.row).ToList();

            if (members.Count == 1)
            {
                result.Add(members[0]);
                continue;
            }

            var totalQuantity = members.Sum(r => r.Quantity);
            var artworkId = members[0].ArtworkId;
            for (var i = 1; i < members.Count; i++)
            {
                artworkId = FoldArtworkId(artworkId, members[i].ArtworkId);
            }

            var latest = members[^1];
            var merged = latest with { Quantity = totalQuantity, ArtworkId = artworkId };
            result.Add(merged);

            logger?.LogWarning(
                "Merged {Count} duplicate rows for OracleId {OracleId} / Condition {Condition} into quantity {Quantity}.",
                members.Count, group.Key.OracleId, group.Key.Condition ?? "(blank)", totalQuantity);
        }

        return result;
    }

    /// <summary>
    /// The one fold rule for <c>ArtworkId</c> everywhere it is needed —
    /// exposed publicly so a collection store built on this codec applies
    /// the identical rule when merging a freshly-committed cohort into
    /// rows this codec already read, rather than re-deriving it. Two nulls
    /// agree (both mean "no printing recorded"), so folding two
    /// <c>Manual</c> rows keeps null; any disagreement between two
    /// non-null values — different printings' art — sets null rather than
    /// picking one, because a wrong printing id is a confidently wrong
    /// answer and a null is an honest "don't know".
    /// </summary>
    public static string? FoldArtworkId(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal) ? a : null;

    // ---------------------------------------------------------------
    // Tokenizer
    // ---------------------------------------------------------------

    private readonly record struct Record(int LineNumber, List<string> Fields);

    /// <summary>
    /// A hand-written RFC 4180 tokenizer over the whole text rather than a
    /// line-by-line reader, because a quoted field may legally contain a
    /// CR or LF — <see cref="TextReader.ReadLine"/> cannot tell that
    /// newline apart from a record terminator, and it would slice a
    /// multi-line quoted field into two malformed records. This drives the
    /// project's requirement that a writer quote on embedded CR/LF as
    /// faithfully as it does on comma or quote.
    ///
    /// <para>
    /// <b>Whitespace before an opening quote is content, not a quoted
    /// field</b> — the override this codec was built against, and the
    /// opposite of <c>Microsoft.VisualBasic.FileIO.TextFieldParser</c>'s
    /// documented <c>BeginQuotesRegex</c> behaviour
    /// (docs/design/collection.md §D1). A field only opens a quoted region
    /// when the very first character read for that field is <c>"</c> —
    /// tracked below via <c>field.Length == 0</c>. If anything (even one
    /// space) was already appended, a later <c>"</c> is ordinary content:
    /// it does not open a quoted region, and it is never doubled-up or
    /// treated as an escape.
    /// </para>
    /// </summary>
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
            throw new NativeCsvFormatException($"Line {recordStartLine}: unterminated quoted field.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new Record(recordStartLine, fields));
        }

        return records;
    }
}
