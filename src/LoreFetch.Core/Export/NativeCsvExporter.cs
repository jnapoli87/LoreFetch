using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;

namespace LoreFetch.Core.Export;

/// <summary>
/// The native format, exposed as an <see cref="ICollectionExporter"/> rather
/// than special-cased on the store — CONTRACTS.md is explicit that the
/// most-used export path must share code and tests with the third-party
/// adapters, not sit outside them with its own, less-scrutinised writer.
/// All of the actual work — quoting, the header, the BOM — lives in
/// <see cref="NativeCsvCodec"/>; this type is the thin
/// <see cref="ICollectionExporter"/> adapter over it plus the
/// <see cref="ExportFormat"/> metadata the picker needs.
/// </summary>
public sealed class NativeCsvExporter : ICollectionExporter
{
    /// <summary>
    /// <c>IsVerified: true</c> — unlike a third-party adapter, the native
    /// format's only "target tool" is this codec's own reader, and that
    /// round trip is exhaustively covered by
    /// <c>NativeCsvCodecRoundTripTests</c>/<c>NativeCsvCodecMergeTests</c>.
    /// There is no external importer whose behaviour could still surprise
    /// us the way it can for Moxfield or Deckbox, so unlike those adapters
    /// this one is not a guess. <c>Notes</c> is null because the native
    /// format is the source of truth: it carries every column at full
    /// fidelity, so there is nothing to caveat the way "printing columns
    /// left blank" caveats an adapter.
    /// </summary>
    public ExportFormat Format { get; } = new(
        Id: "native",
        DisplayName: "Native (LoreFetch CSV)",
        FileExtension: "csv",
        IsVerified: true,
        Notes: null);

    /// <summary>
    /// Writes UTF-8 <b>with</b> BOM — CLAUDE.md §Storage: this is the file
    /// a user opens directly in Excel, which mangles non-ASCII card names
    /// without one. Never disposes <paramref name="destination"/>; the
    /// caller opened it and owns its lifetime (CONTRACTS.md's mechanic 1 —
    /// every <c>ICollectionExporter</c> must use the <c>leaveOpen: true</c>
    /// overload, which <see cref="NativeCsvCodec.WriteAsync(Stream,IReadOnlyList{CollectionRow},CancellationToken)"/>
    /// already does).
    /// </summary>
    public Task ExportAsync(IReadOnlyList<CollectionRow> rows, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(destination);

        return NativeCsvCodec.WriteAsync(destination, rows, ct);
    }
}
