using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using OpenCvSharp;

namespace LoreFetch.Lab.RoundTrip;

/// One sampled artwork's measured outcome. `ImageAvailable = false` means
/// the cache had no (or an undecodable) file for this artwork -- every
/// other field is then meaningless (`-1`/`null`) rather than a real
/// measurement, and callers must exclude it from rate/margin statistics
/// rather than silently counting it as a miss.
public sealed record RoundTripSampleOutcome(
    string ArtworkId,
    string OracleId,
    bool IsBasicLand,
    bool ImageAvailable,
    string? Rank1ArtworkId,
    string? Rank1OracleId,
    int Rank1Distance,
    int OwnDistance,
    int BestOtherDistance,
    string? BestOtherArtworkId)
{
    /// The B2 gate's actual pass/fail bit for this sample: rank 1 named
    /// THIS artwork, not merely this oracle card (CLAUDE.md "the one gate
    /// that matters most": "Assert on ArtworkId"). `false` for a missing
    /// image, never counted as a pass.
    public bool IsCorrect => ImageAvailable && string.Equals(Rank1ArtworkId, ArtworkId, StringComparison.Ordinal);

    /// How much closer the true match is than the best impostor entry.
    /// Meaningless (and not used) when `ImageAvailable` is false.
    public int Margin => BestOtherDistance - OwnDistance;
}

/// Measures ONE sampled artwork through two layers, both built from public
/// building blocks the shipping query path already calls -- never a third
/// transform:
///
///   1. `ICardIdentifier.Identify` itself -- exactly the call the scanner
///      makes -- for the gate's primary assertion (rank 1's `ArtworkId`).
///   2. A raw scan over the loaded index's own entries, for the MARGIN --
///      "the distance to the best DIFFERENT-ARTWORK entry" -- which
///      `Identify`'s contract cannot answer by itself. `ICardIdentifier`
///      deliberately collapses its result to the best DISTINCT `OracleId`
///      (CONTRACTS.md; `HashCardIdentifier`'s own doc comment: "best
///      distance per oracle across all of its arts"), so a heavily
///      reprinted card's OWN sibling arts never appear as separate
///      candidates -- exactly the artwork-granularity gap CLAUDE.md's gate
///      exists to catch ("a gate asserting only OracleId passes while
///      matching a different art of the same card"). The raw scan below
///      recomposes ONLY `QueryTransform.Prepare`, `Cv2.Flip(...,
///      FlipMode.XY)`, `CardHasher.Hash` and `CardHash.HammingDistance` --
///      the exact combination `HashCardIdentifier`'s own (private)
///      `HashBothOrientations` uses -- so this is the one query path
///      called from outside the interface boundary, not a reimplementation
///      of it.
public static class RoundTripMeasurement
{
    public static RoundTripSampleOutcome Measure(
        HashCardIdentifier identifier,
        HashIndexData index,
        RectifiedCard card,
        HashIndexEntry queriedEntry,
        int maxCandidates = 3)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(card);

        // Layer 1: the shipping seam, exactly as the scanner calls it.
        var candidates = identifier.Identify(card, maxCandidates);
        var rank1 = candidates.Count > 0 ? candidates[0] : (CardCandidate?)null;

        // Layer 2: the raw entry-level scan for the margin. Both
        // orientations, matching HashCardIdentifier's own internal
        // combination exactly -- an upside-down render is not expected
        // from Scryfall, but computing only one orientation here would
        // silently understate a real impostor's distance if its OWN
        // matching entry happened to be closer when flipped, which would
        // make the reported margin look better than the shipping path
        // would actually deliver.
        using var uprightGray = QueryTransform.Prepare(card);
        var uprightHash = CardHasher.Hash(uprightGray);
        using var flippedGray = new Mat();
        Cv2.Flip(uprightGray, flippedGray, FlipMode.XY);
        var flippedHash = CardHasher.Hash(flippedGray);

        var ownDistance = -1;
        var foundOwn = false;
        var bestOtherDistance = int.MaxValue;
        string? bestOtherArtworkId = null;

        foreach (var entry in index.Entries)
        {
            var distance = Math.Min(
                entry.Hash.HammingDistance(uprightHash),
                entry.Hash.HammingDistance(flippedHash));

            // Excluded by ARTWORK, not by oracle -- a different printing of
            // the SAME oracle card is exactly the kind of "best different
            // artwork" impostor this margin exists to catch (see the
            // type's own doc comment). Excluding the whole oracle here
            // would be the bug RoundTripMeasurementTests's own chaos case
            // demonstrates.
            if (string.Equals(entry.ArtworkId, queriedEntry.ArtworkId, StringComparison.Ordinal))
            {
                ownDistance = distance;
                foundOwn = true;
                continue;
            }

            if (distance < bestOtherDistance)
            {
                bestOtherDistance = distance;
                bestOtherArtworkId = entry.ArtworkId;
            }
        }

        if (!foundOwn)
        {
            throw new InvalidOperationException(
                $"RoundTripMeasurement: queried artwork \"{queriedEntry.ArtworkId}\" was not found among the " +
                "loaded index's own entries -- the index handed to Measure must be the SAME one the sample was drawn from.");
        }

        return new RoundTripSampleOutcome(
            queriedEntry.ArtworkId,
            queriedEntry.OracleId,
            queriedEntry.IsBasicLand,
            ImageAvailable: true,
            rank1?.ArtworkId,
            rank1?.OracleId,
            rank1?.Distance ?? -1,
            ownDistance,
            bestOtherDistance,
            bestOtherArtworkId);
    }

    /// The placeholder outcome for a sampled artwork whose cache image is
    /// missing or undecodable -- kept in the outcome list (rather than
    /// dropped) so a caller can still see WHICH artworks were skipped,
    /// while `RoundTripGateStatistics` excludes it from every rate and
    /// distance statistic via `ImageAvailable`.
    public static RoundTripSampleOutcome MissingImageOutcome(HashIndexEntry entry) => new(
        entry.ArtworkId, entry.OracleId, entry.IsBasicLand, ImageAvailable: false,
        Rank1ArtworkId: null, Rank1OracleId: null, Rank1Distance: -1, OwnDistance: -1, BestOtherDistance: -1, BestOtherArtworkId: null);
}
