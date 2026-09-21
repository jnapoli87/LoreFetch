namespace LoreFetch.Core.Abstractions;

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

public interface ICollectionStore
{
    /// Commits every tile whose State is Included or ManuallySet.
    /// Tile → row: Chosen gives OracleId/OracleName; ManuallySet → Source.Manual
    /// with BestMatchDistance null; Included → Source.Hash with ChosenDistance.
    /// Returns the number of CARDS committed — the sum of the quantity
    /// increments, not the number of rows touched. Committing nine basic
    /// lands returns 9, which is the number a UI shows the user; the rows
    /// affected would be 1 and would read as a bug.
    /// Duplicate tiles within one cohort are folded before writing.
    /// Throws CollectionStoreException when the file cannot be replaced —
    /// the caller keeps its cohort and can retry.
    Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct);

    Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct);
}

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
