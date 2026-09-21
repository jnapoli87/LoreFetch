using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Configurable `ExportFormat`, so a caller can register one verified and
/// one UNVERIFIED instance side by side and exercise the `IsVerified` badge
/// in the export picker — that configurability is the whole reason this
/// fake exists rather than a single hardcoded format.
public sealed class StubCollectionExporter : ICollectionExporter
{
    public StubCollectionExporter(ExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Format = format;
    }

    public ExportFormat Format { get; }

    /// Writes a trivial, human-readable file to `destination` — enough to
    /// prove an export happened, nothing more. Never closes `destination`;
    /// the caller opened it and the caller owns its lifetime.
    public async Task ExportAsync(IReadOnlyList<CollectionRow> rows, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(destination);

        var writer = new StreamWriter(destination, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync($"# {Format.DisplayName} ({Format.Id})").ConfigureAwait(false);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"{row.OracleName},{row.Quantity}").ConfigureAwait(false);
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
