using LoreFetch.Core.Identification;

namespace LoreFetch.Lab.RoundTrip;

/// Deterministic sampling over the COMMITTED hash index's own entries --
/// never the bulk manifest (`scryfall-bulk/filtered-artworks.jsonl`), which
/// is gitignored and can drift day to day (orchestration "Machine split"
/// rule 4: a fresh `bulk` pull "drifts from the committed index"). Every
/// field a sample needs -- `ArtworkId`, `OracleId`, `IsBasicLand` -- is
/// already on `HashIndexEntry`, and `cards.lfidx` is the one artifact this
/// package's brief has already verified byte-identical across machines
/// (SHA-256 `6495314e...`), so sampling from it is what makes "the same
/// seed picks the same 200 artworks on the Mac and on the PC" an actual
/// guarantee rather than a hope.
public static class RoundTripSampler
{
    /// Picks `landCount` basic-land entries and `nonLandCount` non-land
    /// entries, each a uniformly random subset of its own population,
    /// concatenated lands-then-non-lands -- a fixed, documented order, so
    /// "the sample" names one specific sequence, not just one set, at a
    /// given `seed`. Both counts clamp to the available population (never
    /// throws for asking for more lands than exist).
    public static IReadOnlyList<HashIndexEntry> SelectStratifiedSample(
        IReadOnlyList<HashIndexEntry> entries, int landCount, int nonLandCount, int seed)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (landCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(landCount), landCount, "landCount must not be negative.");
        }

        if (nonLandCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nonLandCount), nonLandCount, "nonLandCount must not be negative.");
        }

        var lands = entries.Where(e => e.IsBasicLand).ToList();
        var nonLands = entries.Where(e => !e.IsBasicLand).ToList();

        // ONE Random, drawn from in this fixed order -- lands first, then
        // non-lands -- so a single seed value is the whole recipe: nothing
        // about the sample depends on how many draws either shuffle takes
        // internally (`ShuffleAndTake` always draws exactly `take` times),
        // so this is stable even if the land/non-land population counts
        // themselves ever change (e.g. a future bulk-data refresh).
        var random = new Random(seed);
        var pickedLands = ShuffleAndTake(lands, landCount, random);
        var pickedNonLands = ShuffleAndTake(nonLands, nonLandCount, random);

        var result = new List<HashIndexEntry>(pickedLands.Count + pickedNonLands.Count);
        result.AddRange(pickedLands);
        result.AddRange(pickedNonLands);
        return result;
    }

    /// A plain uniform sample over every entry, land or not -- used by the
    /// query-hash witness, which does not need land/non-land stratification
    /// (that is a B2 round-trip-gate concern; the witness only cares about
    /// query-side hash bits, and a land's hash bits are not special).
    public static IReadOnlyList<HashIndexEntry> SelectUniformSample(
        IReadOnlyList<HashIndexEntry> entries, int count, int seed)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must not be negative.");
        }

        var random = new Random(seed);
        return ShuffleAndTake(entries.ToList(), count, random);
    }

    /// Partial Fisher-Yates over a COPY of `source` (the caller's list is
    /// never mutated), stopping after `take` swaps: the first `take`
    /// elements of `working` are then a uniformly random `take`-subset of
    /// `source`, in random order, using EXACTLY `take` draws from `random`
    /// regardless of `source.Count` -- which is what lets
    /// `SelectStratifiedSample` chain two shuffles off one seed
    /// deterministically (the land shuffle's draw count depends only on
    /// `landCount`, never on how many lands exist).
    private static List<HashIndexEntry> ShuffleAndTake(List<HashIndexEntry> source, int count, Random random)
    {
        var take = Math.Min(count, source.Count);
        var working = new List<HashIndexEntry>(source);
        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, working.Count);
            (working[i], working[j]) = (working[j], working[i]);
        }

        return working.GetRange(0, take);
    }
}
