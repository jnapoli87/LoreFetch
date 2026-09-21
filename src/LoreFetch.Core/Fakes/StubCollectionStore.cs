using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// In-memory rows with the real dedup and commit semantics from
/// docs/CONTRACTS.md §"Collection and export" — so stream A's collection
/// view, empty state and export picker can all be built and tested against
/// this before `Core/Collection` (stream D) exists.
public sealed class StubCollectionStore : ICollectionStore
{
    private readonly object _lock = new();
    private readonly List<CollectionRow> _rows = new();
    private bool _throwOnNextCommit;

    /// Arms the store to throw `CollectionStoreException` on the very next
    /// `CommitCohortAsync` call — the fake's substitute for "Excel has the
    /// file locked". The store's contents are left exactly as they were: the
    /// contract's retry story only holds if the failed attempt wrote
    /// nothing, so the check for a mutation happens before any row is
    /// touched. Consumed by that one call; the call after it commits
    /// normally.
    public void ArmNextCommitToThrow()
    {
        lock (_lock)
        {
            _throwOnNextCommit = true;
        }
    }

    /// Test-only seam, not part of `ICollectionStore`: preloads a row
    /// directly, bypassing `CommitCohortAsync`. Nothing on `Cohort`/
    /// `CohortTile` carries a `Condition` — v1 never assesses one — so this
    /// is the only way a test can put a row with a non-null `Condition` into
    /// the store, which is what's needed to exercise "dedup keys on OracleId
    /// **and** Condition" against a second, differently-conditioned row for
    /// the same card.
    public void Seed(CollectionRow row)
    {
        lock (_lock)
        {
            _rows.Add(row);
        }
    }

    /// Commits every tile whose State is Included or ManuallySet; Excluded
    /// and Unresolved tiles are skipped entirely. Duplicate tiles within the
    /// cohort are folded before touching the row list. Returns the number of
    /// CARDS committed — the sum of the quantity increments, not the number
    /// of rows touched.
    public Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_throwOnNextCommit)
            {
                _throwOnNextCommit = false;
                throw new CollectionStoreException("Simulated write failure (StubCollectionStore) — the file is 'locked'.");
            }

            // Fold duplicate tiles within this cohort first, keyed exactly
            // as the store itself keys rows: OracleId + Condition. Condition
            // is always null here — nothing upstream of the store ever sets
            // one in v1 — but the key is still built from both fields so the
            // fold logic and the store's own dedup logic can never drift
            // apart.
            var folded = new Dictionary<(string OracleId, string? Condition), FoldedRow>();
            var cardsCommitted = 0;

            foreach (var tile in cohort.Tiles)
            {
                if (tile.State != TileState.Included && tile.State != TileState.ManuallySet)
                {
                    continue;
                }

                var chosen = tile.Chosen
                    ?? throw new InvalidOperationException(
                        "Included/ManuallySet tile has a null Chosen — violates the CohortTile contract.");

                const string? condition = null; // v1 never assesses a condition.
                var key = (chosen.OracleId, condition);
                var source = tile.State == TileState.ManuallySet ? RowSource.Manual : RowSource.Hash;
                var distance = tile.State == TileState.ManuallySet ? null : tile.ChosenDistance;
                var artworkId = tile.State == TileState.ManuallySet ? null : tile.ChosenArtworkId;

                cardsCommitted++;

                folded[key] = folded.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        Quantity = existing.Quantity + 1,
                        LastScannedAt = cohort.CapturedAt > existing.LastScannedAt ? cohort.CapturedAt : existing.LastScannedAt,
                        BestMatchDistance = distance,
                        Source = source,
                        ArtworkId = AgreeOrNull(existing.ArtworkId, artworkId),
                    }
                    : new FoldedRow(chosen.OracleName, 1, cohort.CapturedAt, distance, source, artworkId);
            }

            foreach (var (key, fold) in folded)
            {
                var existingIndex = _rows.FindIndex(r => r.OracleId == key.OracleId && r.Condition == key.Condition);
                if (existingIndex >= 0)
                {
                    var existingRow = _rows[existingIndex];
                    _rows[existingIndex] = existingRow with
                    {
                        Quantity = existingRow.Quantity + fold.Quantity,
                        LastScannedAt = fold.LastScannedAt > existingRow.LastScannedAt ? fold.LastScannedAt : existingRow.LastScannedAt,
                        BestMatchDistance = fold.BestMatchDistance,
                        Source = fold.Source,
                        ArtworkId = AgreeOrNull(existingRow.ArtworkId, fold.ArtworkId),
                    };
                }
                else
                {
                    _rows.Add(new CollectionRow(
                        key.OracleId, fold.OracleName, fold.Quantity, key.Condition,
                        fold.LastScannedAt, fold.BestMatchDistance, fold.Source, fold.ArtworkId));
                }
            }

            return Task.FromResult(cardsCommitted);
        }
    }

    public Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CollectionRow>>(_rows.ToList());
        }
    }

    /// Agree-or-null, the contract's fold rule for ArtworkId. Two copies of
    /// one oracle card in the same condition merge into a single row; if they
    /// came from different printings their art ids differ, and the merged row
    /// must then say it does not know rather than pick one. Last-write-wins
    /// would leave a column right sometimes and wrong sometimes, with nothing
    /// able to tell which — and a wrong printing yields a confidently wrong
    /// price. Two nulls agree, so merging Manual rows keeps null.
    private static string? AgreeOrNull(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal) ? a : null;

    private sealed record FoldedRow(
        string OracleName,
        int Quantity,
        DateTimeOffset LastScannedAt,
        int? BestMatchDistance,
        RowSource Source,
        string? ArtworkId);
}
