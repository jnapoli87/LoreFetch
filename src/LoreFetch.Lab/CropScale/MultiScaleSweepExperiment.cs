using LoreFetch.Core.Identification;
using OpenCvSharp;

namespace LoreFetch.Lab.CropScale;

/// Package B5c's decision support for the "fix belongs in the identifier"
/// option: measures what a `HashCardIdentifier`-internal N-scale sweep
/// would do to queries that are ALREADY well-framed (no crop error at
/// all) -- the risk `CropScaleRealFrameTests` cannot see on its own,
/// because that test only asks whether a sweep RECOVERS the one known
/// failure, never whether trying extra scales makes an already-correct
/// query worse. This type is experimental, NOT a second `ICardIdentifier`
/// (that interface is frozen -- DECISIONS.md/CONTRACTS.md): it reproduces
/// `HashCardIdentifier`'s own per-oracle best-of-both-orientations
/// reduction (`HashBothOrientations` + `RankOracles`'s "best distance per
/// oracle"), generalized over several CANDIDATE query scales instead of
/// one, so a caller can see the actual rank-1 and margin impact before any
/// production code is written.
public static class MultiScaleSweepExperiment
{
    /// For ONE already-decoded, well-framed (no crop error) canonical
    /// color render: computes the best distance per oracle a hypothetical
    /// sweep would report, by taking the elementwise minimum, across every
    /// `scale` in `scales` AND both orientations, of the same raw Hamming
    /// scan `RoundTripMeasurement`'s own margin computation performs --
    /// never a threshold, never an early exit, matching DECISIONS.md "Step 7".
    /// Returns the swept rank-1 oracle id and its distance, plus the
    /// swept margin to the best OTHER oracle -- comparable field-for-field
    /// with `RoundTripSampleOutcome`.
    public static (string Rank1OracleId, int Rank1Distance, int OwnDistance, int Margin) IdentifyWithSweep(
        HashIndexData index, Mat canonicalColorBgr, string queriedOracleId, string queriedArtworkId,
        IReadOnlyList<(float WidthInset, float HeightInset)> scales)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(canonicalColorBgr);
        ArgumentNullException.ThrowIfNull(scales);
        if (scales.Count == 0)
        {
            throw new ArgumentException("At least one scale is required.", nameof(scales));
        }

        // One (upright, flipped) hash pair per candidate scale -- exactly
        // HashCardIdentifier.HashBothOrientations, repeated per scale
        // instead of once.
        var hashVariants = new List<CardHash>(scales.Count * 2);
        foreach (var (widthInset, heightInset) in scales)
        {
            using var scaled = CropScaleTransform.Apply(canonicalColorBgr, widthInset, heightInset);
            var card = LoreFetch.Lab.RoundTrip.CardImageLoader.ToRectifiedCard(scaled);
            using var gray = QueryTransform.Prepare(card);
            hashVariants.Add(CardHasher.Hash(gray));

            using var flippedGray = new Mat();
            Cv2.Flip(gray, flippedGray, FlipMode.XY);
            hashVariants.Add(CardHasher.Hash(flippedGray));
        }

        var bestDistanceByOracle = new Dictionary<string, int>(StringComparer.Ordinal);
        var ownDistance = -1;
        var foundOwn = false;

        foreach (var entry in index.Entries)
        {
            var best = int.MaxValue;
            foreach (var variant in hashVariants)
            {
                var d = entry.Hash.HammingDistance(variant);
                if (d < best)
                {
                    best = d;
                }
            }

            if (string.Equals(entry.ArtworkId, queriedArtworkId, StringComparison.Ordinal))
            {
                ownDistance = best;
                foundOwn = true;
            }

            if (!bestDistanceByOracle.TryGetValue(entry.OracleId, out var currentBest) || best < currentBest)
            {
                bestDistanceByOracle[entry.OracleId] = best;
            }
        }

        if (!foundOwn)
        {
            throw new InvalidOperationException(
                $"MultiScaleSweepExperiment: queried artwork \"{queriedArtworkId}\" was not found in the index.");
        }

        var ranked = bestDistanceByOracle.OrderBy(kv => kv.Value).ToList();
        var rank1 = ranked[0];
        var bestOther = ranked.Where(kv => kv.Key != queriedOracleId).Select(kv => kv.Value).DefaultIfEmpty(int.MaxValue).Min();

        return (rank1.Key, rank1.Value, ownDistance, bestOther - ownDistance);
    }
}
