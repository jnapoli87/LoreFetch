namespace LoreFetch.Core.Abstractions;

public readonly record struct CardCandidate(
    string OracleId,     // Scryfall oracle_id — the identity key
    string OracleName,
    int Distance,         // Hamming, 0..1024 — lower is closer
    string? ArtworkId);   // Scryfall printing id of the matched ART; null for stubs

public interface ICardIdentifier
{
    /// Identifies the implementation in logs and the accuracy table.
    /// e.g. "CardSpotterHash/v1", "Stub"
    string Name { get; }

    /// The maxCandidates nearest **distinct oracle cards**, ranked by
    /// ascending Distance — best distance per OracleId, never the same card
    /// twice under two of its artworks. A heavily reprinted card otherwise
    /// fills the whole list with its own arts, and the distance margin that
    /// calibrates the thresholds would then measure art-vs-art of one card
    /// instead of a genuine confusion.
    /// NEVER filters by threshold — thresholds are applied by the scan
    /// pipeline, in one place. Empty only if the index is empty.
    IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates);
}

public readonly record struct OracleEntry(string OracleId, string OracleName);

/// Every oracle card the identifier can return. Backs the
/// "Set card manually…" type-ahead.
public interface IOracleCatalog
{
    IReadOnlyList<OracleEntry> All { get; }
}
