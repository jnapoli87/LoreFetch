using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Core.Collection;

/// <summary>
/// <see cref="ICollectionStore"/> over the native CSV format at a fixed
/// path. Parsing, quoting and duplicate-row folding all live in
/// <see cref="NativeCsvCodec"/>; this type owns only what the codec
/// deliberately knows nothing about — the file on disk, the cohort→row
/// fold, and the atomic write sequence.
///
/// <para>
/// <b>The Manual-row decision (open thread S0.6b).</b> CONTRACTS.md's
/// <c>CommitCohortAsync</c> doc comment says a <c>ManuallySet</c> tile
/// commits with <c>BestMatchDistance</c> AND <c>ArtworkId</c> null.
/// <c>CohortTile.SetManually</c> already nulls both on the tile itself, so
/// in practice trusting <c>tile.ChosenDistance</c>/<c>tile.ChosenArtworkId</c>
/// would currently read as null anyway — but this store does not trust
/// that invariant. It derives both from <c>tile.State</c> instead: when the
/// state is <see cref="TileState.ManuallySet"/>, the store hardcodes null
/// for both fields regardless of what the tile happens to be carrying. A
/// manual pick names a CARD, not a printing (see
/// <c>CohortTile.SetManually</c>'s own doc comment), so a non-null value
/// there would misattribute a printing to a choice the user made on other
/// grounds — and a future change to <c>CohortTile</c> that stopped nulling
/// these on <c>SetManually</c> would otherwise leak a hash guess into a row
/// the user believes they corrected. Deriving from state, not trusting the
/// tile, is what a chaos test can actually catch — see
/// <see cref="FoldCohort"/>.
/// </para>
/// </summary>
public sealed class CsvCollectionStore : ICollectionStore
{
    private readonly string _path;
    private readonly ILogger<CsvCollectionStore> _logger;

    // Serialises every commit/list against this store instance: the UI may
    // ask to list the collection while a commit is mid-flight, and two
    // overlapping commits reading the same "existing rows" snapshot would
    // silently lose one of them. One instance == one file, so a single
    // semaphore is the whole story; nothing here helps two *separate*
    // CsvCollectionStore instances pointed at the same path, which v1 never
    // creates.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CsvCollectionStore(string path, ILogger<CsvCollectionStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = Path.GetFullPath(path);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        ct.ThrowIfCancellationRequested();

        // Folding — and the null-Chosen invariant check inside it — needs no
        // file access, so it happens before the gate: a broken cohort fails
        // fast without blocking a concurrent, valid commit or list.
        var (folded, cardsCommitted) = FoldCohort(cohort);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ReadRowsFromDiskAsync(ct).ConfigureAwait(false);
            var merged = MergeIntoExisting(existing, folded);
            await WriteAtomicallyAsync(merged, ct).ConfigureAwait(false);
            return cardsCommitted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadRowsFromDiskAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // -----------------------------------------------------------------
    // Cohort -> row fold
    // -----------------------------------------------------------------

    private readonly record struct FoldedCommitRow(
        string OracleName, int Quantity, DateTimeOffset LastScannedAt,
        int? BestMatchDistance, RowSource Source, string? ArtworkId);

    /// <summary>
    /// Folds every <c>Included</c>/<c>ManuallySet</c> tile in one cohort
    /// into at most one row per <c>OracleId</c> (v1 never assesses a
    /// <c>Condition</c>, so the key's second half is always null here) —
    /// mirrors <c>Fakes/StubCollectionStore</c>'s semantics, not its code,
    /// per the work package. Nine tiles of one basic land fold to a single
    /// entry with <c>Quantity == 9</c>; <c>cardsCommitted</c> still counts 9,
    /// because that — not "rows touched" — is what
    /// <see cref="ICollectionStore.CommitCohortAsync"/> documents as the
    /// return value.
    /// </summary>
    private static (Dictionary<(string OracleId, string? Condition), FoldedCommitRow> Folded, int CardsCommitted) FoldCohort(Cohort cohort)
    {
        var folded = new Dictionary<(string, string?), FoldedCommitRow>();
        var cardsCommitted = 0;

        foreach (var tile in cohort.Tiles)
        {
            if (tile.State != TileState.Included && tile.State != TileState.ManuallySet)
            {
                continue; // Excluded and Unresolved tiles are never committed.
            }

            var chosen = tile.Chosen
                ?? throw new InvalidOperationException(
                    "An Included/ManuallySet tile has a null Chosen — this violates the CohortTile " +
                    "contract (Chosen is documented null only when Unresolved) and is a broken " +
                    "invariant, not a data condition worth writing a row for.");

            var source = tile.State == TileState.ManuallySet ? RowSource.Manual : RowSource.Hash;

            // Derived from `source`, never read from the tile when Manual —
            // see this class's doc comment for why that distinction matters
            // even though CohortTile itself already nulls both on a manual
            // set.
            var distance = source == RowSource.Manual ? (int?)null : tile.ChosenDistance;
            var artworkId = source == RowSource.Manual ? null : tile.ChosenArtworkId;

            const string? condition = null; // CohortTile carries no condition in v1.
            var key = (chosen.OracleId, condition);
            cardsCommitted++;

            folded[key] = folded.TryGetValue(key, out var existing)
                ? existing with
                {
                    Quantity = existing.Quantity + 1,
                    LastScannedAt = cohort.CapturedAt,
                    BestMatchDistance = distance,
                    Source = source,
                    ArtworkId = NativeCsvCodec.FoldArtworkId(existing.ArtworkId, artworkId),
                }
                : new FoldedCommitRow(chosen.OracleName, 1, cohort.CapturedAt, distance, source, artworkId);
        }

        return (folded, cardsCommitted);
    }

    /// <summary>
    /// Merges the cohort's folded rows into the rows already on disk, using
    /// the identical key (<c>OracleId</c> + <c>Condition</c>) and the
    /// identical <see cref="NativeCsvCodec.FoldArtworkId"/> rule the codec
    /// applies when it finds duplicates already sitting in one file — so
    /// "two scans of the same card, one per cohort" and "two duplicate rows
    /// found in one file" resolve identically. Ties (equal timestamps)
    /// prefer the incoming commit, since it is, by construction, the more
    /// recent event.
    /// </summary>
    private static List<CollectionRow> MergeIntoExisting(
        List<CollectionRow> existing,
        Dictionary<(string OracleId, string? Condition), FoldedCommitRow> folded)
    {
        var result = new List<CollectionRow>(existing);

        foreach (var (key, fold) in folded)
        {
            var index = result.FindIndex(r =>
                r.OracleId == key.OracleId && string.Equals(r.Condition, key.Condition, StringComparison.Ordinal));

            if (index >= 0)
            {
                var row = result[index];
                var incomingIsNewer = fold.LastScannedAt >= row.LastScannedAt;
                result[index] = row with
                {
                    OracleName = incomingIsNewer ? fold.OracleName : row.OracleName,
                    Quantity = row.Quantity + fold.Quantity,
                    LastScannedAt = incomingIsNewer ? fold.LastScannedAt : row.LastScannedAt,
                    BestMatchDistance = incomingIsNewer ? fold.BestMatchDistance : row.BestMatchDistance,
                    Source = incomingIsNewer ? fold.Source : row.Source,
                    ArtworkId = NativeCsvCodec.FoldArtworkId(row.ArtworkId, fold.ArtworkId),
                };
            }
            else
            {
                result.Add(new CollectionRow(
                    key.OracleId, fold.OracleName, fold.Quantity, key.Condition,
                    fold.LastScannedAt, fold.BestMatchDistance, fold.Source, fold.ArtworkId));
            }
        }

        return result;
    }

    // -----------------------------------------------------------------
    // Disk I/O
    // -----------------------------------------------------------------

    /// A missing file means "no collection yet" (first run), not an error —
    /// CONTRACTS.md's D0 item 7. Any other read failure (most often another
    /// program holding the file open) becomes a CollectionStoreException so
    /// the caller can retry rather than lose its pending cohort.
    private async Task<List<CollectionRow>> ReadRowsFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var rows = await NativeCsvCodec.ReadAsync(stream, _logger, ct).ConfigureAwait(false);
            return [.. rows];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CollectionStoreException($"Could not read the collection at '{_path}'.", ex);
        }
    }

    /// <summary>
    /// The exact sequence CONTRACTS.md and DECISIONS.md §Storage specify, in
    /// the order that keeps the target present throughout:
    /// </summary>
    /// <list type="number">
    /// <item>Write the full new content to a fresh temp file in the
    /// TARGET's own directory — never <see cref="Path.GetTempPath"/>.
    /// <c>File.Move</c> always permits <c>MOVEFILE_COPY_ALLOWED</c>, so a
    /// cross-volume move silently degrades to copy-then-delete and the
    /// whole atomicity guarantee disappears without an error.</item>
    /// <item><c>Flush(true)</c> the temp <see cref="FileStream"/> before
    /// disposing it — <c>Dispose</c> alone flushes to the OS, not to disk.</item>
    /// <item>Copy the CURRENT target to <c>.bak</c> — before the rename, so
    /// the target is never briefly absent and a failure here leaves the
    /// original completely untouched (the rename hasn't happened yet).
    /// Skipped on a first-ever write, when there is nothing to back up.</item>
    /// <item><c>File.Move(temp, target, overwrite: true)</c> — not
    /// <c>File.Replace</c>, which cannot create the target on a first write
    /// and documents its own steps as non-atomic on Unix.</item>
    /// </list>
    /// <para>
    /// Any I/O failure at any step — most often the target locked open by
    /// another program — throws <see cref="CollectionStoreException"/>,
    /// deletes the stray temp file, and leaves the target byte-identical to
    /// what it was before the call.
    /// </para>
    private async Task WriteAtomicallyAsync(IReadOnlyList<CollectionRow> rows, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = _path + ".bak";

        try
        {
            await using (var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await NativeCsvCodec.WriteAsync(tempStream, rows, ct).ConfigureAwait(false);
                await tempStream.FlushAsync(ct).ConfigureAwait(false);
                tempStream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Copy(_path, backupPath, overwrite: true);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteBestEffort(tempPath);
            throw new CollectionStoreException($"Could not write the collection to '{_path}'.", ex);
        }
    }

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort only: we are already inside a failure path and
            // must not let temp-file cleanup mask, or itself become, the
            // real error the caller needs to see.
        }
    }
}
