using System.Globalization;
using System.Text;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;

namespace LoreFetch.Core.Export;

/// <summary>
/// Export adapter that projects the native collection into Moxfield's CSV
/// import shape. Lossy by design: <c>Edition</c>, <c>Collector Number</c>
/// and all other printing-level columns are left blank because v1 identifies
/// the oracle card only — fabricating a set code would introduce false
/// precision into the user's collection data.
///
/// <para>
/// Header and column ordering follow the Moxfield import specification
/// (https://moxfield.com/help/help-articles/importing-collection). This is
/// a community-documented format, not a versioned API, so it may drift.
/// The source URL is repeated at the <see cref="HeaderLine"/> constant and
/// on the <see cref="Format"/> notes so a future maintainer knows where to
/// re-verify.
/// </para>
///
/// <para>
/// No BOM: Moxfield's importer demands exact header matching "including case"
/// and warns about stray characters around headers — a UTF-8 BOM prepended
/// to <c>Count</c> is a plausible break. (docs/stream-d-export.md §Fallbacks).
/// The native format keeps its BOM because it is what users open in Excel;
/// this adapter serves an importer, not a human reader, so BOM-less UTF-8 is
/// the safer default.
/// </para>
///
/// <para>
/// Quoting delegates to <see cref="NativeCsvCodec.QuoteField"/> — one RFC 4180
/// implementation for every format this project writes, never two that can
/// drift apart.
/// </para>
/// </summary>
public sealed class MoxfieldCsvExporter : ICollectionExporter
{
    // Moxfield import specification:
    // https://moxfield.com/help/help-articles/importing-collection
    // Community-documented format — verify before each release.
    // Column order is explicitly irrelevant per the spec; header spelling and
    // case MUST match exactly. Only "Name" is strictly required; every other
    // column is optional and v1 populates only "Count".
    private const string HeaderLine =
        "Count,Name,Edition,Condition,Language,Foil,Collector Number,Alter,Playtest Card,Purchase Price";

    // RFC 4180 record terminator, written explicitly — see NativeCsvCodec
    // for the reasoning (bit-for-bit identical on Windows and macOS CI).
    private const string RecordTerminator = "\r\n";

    /// <summary>
    /// <c>IsVerified: false</c> — no generated file has yet been imported
    /// into a live Moxfield account and confirmed to land correctly.
    /// D4 in docs/stream-d-export.md is the step that verifies this and
    /// should flip the flag. <c>Notes</c> explains the two permanent
    /// limitations: printing columns (Edition, Collector Number) are always
    /// blank because v1 cannot resolve printings, and the format has not been
    /// verified by a real import.
    /// </summary>
    public ExportFormat Format { get; } = new(
        Id: "moxfield",
        DisplayName: "Moxfield (CSV)",
        FileExtension: "csv",
        IsVerified: false,
        Notes: "Edition and Collector Number columns are left blank — v1 identifies the oracle card only, not a specific printing. " +
               "Condition is left blank; Moxfield will apply its own default (typically Near Mint) on import. " +
               "This adapter has not yet been verified by a real import into a live Moxfield account (D4, docs/stream-d-export.md).");

    /// <summary>
    /// Writes the Moxfield CSV header followed by one line per row, populating
    /// only <c>Count</c> and <c>Name</c>. Every other column is an empty field —
    /// never the literal <c>null</c>, never a fabricated set code.
    /// Does not dispose <paramref name="destination"/>; the caller owns it
    /// (CONTRACTS.md mechanic 1 — every <c>ICollectionExporter</c> uses
    /// <c>leaveOpen: true</c>).
    /// </summary>
    public async Task ExportAsync(IReadOnlyList<CollectionRow> rows, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(destination);

        // No BOM — Moxfield's importer requires exact header matching and
        // warns about stray characters; a BOM welded to "Count" is a plausible
        // break. encoderShouldEmitUTF8Identifier: false gives BOM-less UTF-8.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var writer = new StreamWriter(destination, encoding, bufferSize: 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(HeaderLine).ConfigureAwait(false);
            await writer.WriteAsync(RecordTerminator).ConfigureAwait(false);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(FormatRow(row)).ConfigureAwait(false);
                await writer.WriteAsync(RecordTerminator).ConfigureAwait(false);
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static string FormatRow(CollectionRow row)
    {
        // Only Count and Name are populated. The remaining eight columns —
        // Edition, Condition, Language, Foil, Collector Number, Alter,
        // Playtest Card, Purchase Price — are emitted as empty fields.
        // v1 has no printing resolution, so fabricating Edition or Collector
        // Number would introduce false precision; Condition is always null
        // in v1 (camera cannot grade a card); the rest are attributes we
        // never assess. Empty is honest; a guessed field looks authoritative
        // and is wrong.
        //
        // Oracle names are written verbatim, never sanitised (CLAUDE.md:
        // "+2 Mace is a real card; don't prefix it"). NativeCsvCodec.QuoteField
        // is the shared RFC 4180 implementation — one quoting path for every
        // format, never two that can drift.
        var count = row.Quantity.ToString(CultureInfo.InvariantCulture);
        var name = NativeCsvCodec.QuoteField(row.OracleName);

        // 10 columns total: Count, Name, then 8 blank fields.
        return $"{count},{name},,,,,,,,";
    }
}
