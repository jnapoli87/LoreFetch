using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Canned candidates with configurable distances, so a UI (or a test) can
/// drive a `CohortTile` into any state the real hash can produce — a
/// confident match, a low-confidence one, or none at all — without a real
/// index. It has no knowledge of `GoodDistance`/`OkDistance`: the caller
/// picks the distances that land on either side of whichever thresholds it
/// is using, and this stub just hands them back.
public sealed class StubCardIdentifier : ICardIdentifier
{
    // Spread wide enough that a caller can request a handful of candidates
    // without configuring anything and still see distinct, ascending
    // distances — useful for tests that only care about ordering/distinctness
    // and don't need a specific number.
    private static readonly IReadOnlyList<int> DefaultDistances =
        new[] { 40, 90, 140, 190, 240, 290 };

    private IReadOnlyList<int> _nextDistances = DefaultDistances;
    private Func<int, int, string?> _artworkIdSelector = DefaultArtworkId;
    private int _identifyCallCount;

    /// Deterministic in the candidate index and INDEPENDENT of the call
    /// index, so two tiles of the same card agree on their art — which is
    /// the ordinary case, and the one that must keep a non-null ArtworkId
    /// through a dedup fold.
    private static string? DefaultArtworkId(int callIndex, int candidateIndex) =>
        $"stub-art-{candidateIndex}";

    public string Name => "Stub";

    /// The Hamming distances `Identify` hands back on its next call, one per
    /// returned candidate. Stays in effect until reassigned — a cohort of N
    /// tiles calls `Identify` N times, and a caller driving "make every tile
    /// low-confidence" would otherwise have to reset this between each one.
    /// Setting an empty list makes `Identify` return no candidates at all
    /// (the "no match" case). Values are sorted ascending before use, so the
    /// caller doesn't have to pre-sort.
    public IReadOnlyList<int> NextDistances
    {
        get => _nextDistances;
        set => _nextDistances = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// Chooses the ArtworkId for each returned candidate, given the
    /// zero-based index of this `Identify` call and of the candidate within
    /// it. Configurable because the collection store's fold rule is
    /// agree-or-null, so a test needs all three shapes:
    ///   - the default AGREES across calls, so repeated scans of one card
    ///     keep their art through a merge;
    ///   - varying on `callIndex` makes two tiles of the same card DISAGREE,
    ///     which must null the merged row's ArtworkId;
    ///   - returning null keeps the "null for stubs" case the contract
    ///     explicitly allows.
    /// Without this the stub returned null unconditionally, which would have
    /// left the ArtworkId column dead on every fakes path — all of Stream 0
    /// and all of stream A — and broken the first time a real index filled
    /// it in.
    public Func<int, int, string?> ArtworkIdSelector
    {
        get => _artworkIdSelector;
        set => _artworkIdSelector = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// Returns at most maxCandidates candidates, ranked by ascending
    /// Distance, each under a distinct synthetic OracleId — this stub never
    /// returns the same oracle card twice, matching the real contract.
    /// NEVER filters by threshold — it has no thresholds to filter by.
    public IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (maxCandidates <= 0 || _nextDistances.Count == 0)
        {
            return Array.Empty<CardCandidate>();
        }

        var callIndex = _identifyCallCount++;
        var ordered = _nextDistances.OrderBy(d => d).Take(maxCandidates).ToList();
        var candidates = new List<CardCandidate>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            candidates.Add(new CardCandidate(
                OracleId: $"stub-oracle-{i}",
                OracleName: $"Stub Card {i}",
                Distance: ordered[i],
                ArtworkId: _artworkIdSelector(callIndex, i)));
        }

        return candidates;
    }
}
