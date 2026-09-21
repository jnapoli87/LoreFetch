namespace LoreFetch.Core.Abstractions;

// NOTE (package S0.3a): `ICollectionStore` is specified in docs/CONTRACTS.md
// immediately alongside these types, but its `CommitCohortAsync(Cohort, ...)`
// member takes `Cohort`, which is owned by package S0.3b and does not exist
// anywhere in this codebase yet. S0.3a's write scope explicitly excludes
// creating `Cohort`/`CohortTile`/`TileState`/`CaptureReason`. Adding
// `ICollectionStore` here without `Cohort` would fail the whole solution's
// build (an undefined-type compile error), and inventing a stand-in `Cohort`
// would be exactly the kind of unilateral change to a frozen contract member
// this package is told not to make. `ICollectionStore` is therefore deferred
// to land in the same file once S0.3b has added `Cohort` — see the STOP
// report for the verbatim interface to add at that point.

public enum RowSource
{
    Hash,
    Manual,
}

public readonly record struct CollectionRow(
    string OracleId,   // Scryfall oracle_id — the identity key
    string OracleName, // denormalised for human readability; never a key
    int Quantity,
    string? Condition, // null = not assessed; v1 never assesses.
                        // null is the ONLY representation of
                        // "unassessed": null serialises to a blank
                        // field, and a blank field parses back to
                        // null — never "". Condition is half the
                        // dedup key, so "" vs null would silently
                        // split one card into two rows.
    DateTimeOffset LastScannedAt,
    int? BestMatchDistance, // null when Source is Manual
    RowSource Source);

/// The collection file could not be read or replaced — most often because
/// Excel holds it open on Windows. Recoverable by construction: the caller
/// keeps the pending cohort intact and retries after the user closes the file.
public sealed class CollectionStoreException : Exception
{
    public CollectionStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed record ExportFormat(
    string Id,          // v1: "native" | "moxfield"  (see below)
    string DisplayName,
    string FileExtension,
    bool IsVerified,    // has a generated file actually been imported into the live tool?
    string? Notes);     // e.g. "printing columns left blank"

public interface ICollectionExporter
{
    ExportFormat Format { get; }

    Task ExportAsync(IReadOnlyList<CollectionRow> rows, Stream destination, CancellationToken ct);
}
