using System.Text.Json;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Images;
using OpenCvSharp;

namespace LoreFetch.Lab.RoundTrip;

public sealed record QueryHashWitnessEntry(string ArtworkId, string QueryHashHex);

/// A committed, deterministic record of the QUERY-SIDE hash (steps 4-6 via
/// `QueryTransform.Prepare` -> `CardHasher.Hash`, no orientation flip -- a
/// Scryfall render is always upright, so there is exactly one query hash
/// per sampled artwork) for a fixed sample of artworks. Its whole purpose
/// is answering a question nothing in this stream has actually measured
/// yet: HOW MUCH does the query side's own `INTER_AREA` step (step 5, the
/// 32x32 resize) actually diverge between ARM64 and x86-64? B1b's golden
/// hashes prove the REFERENCE transform differs (that is WHY they are
/// `WindowsOnly`); this witness is the query-side equivalent measurement,
/// deliberately NOT skipped on macOS, because the Mac is where this
/// particular measurement needs to be taken.
///
/// `Entries` is sorted by `ArtworkId` (ordinal) rather than left in
/// whatever order sampling drew them -- decoupling "which artworks were
/// picked" (seeded random, see `RoundTripSampler`) from "what order they
/// print in" (alphabetical), so a diff between two runs is never noise
/// from iteration order.
public sealed record QueryHashWitnessDocument(
    string MeasuredOn,
    string MeasuredOnDetail,
    string IndexSha256,
    int SampleSize,
    int Seed,
    DateTimeOffset MeasuredAtUtc,
    IReadOnlyList<QueryHashWitnessEntry> Entries)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Write(string path, QueryHashWitnessDocument document)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(document);

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public static QueryHashWitnessDocument Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<QueryHashWitnessDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException($"Query-hash witness file parsed to null: \"{path}\".");
    }
}

/// A regeneration of query-side hashes for an EXPLICIT, caller-supplied
/// list of artwork ids -- as opposed to `QueryHashWitnessBuilder.Build`,
/// which draws its own sample via `RoundTripSampler`. This is what
/// `Compare`-against-the-committed-witness must use: the committed
/// witness names 50 specific `ArtworkId`s, and re-sampling from whatever
/// index happens to be loaded now silently draws a DIFFERENT 50 the
/// moment the index is rebuilt (entry order and population both change),
/// which then reads as "48/50 query hashes differ" when the real story is
/// "48/50 were never computed against the same artwork at all." Query
/// hashes depend only on the render bytes and the query transform, never
/// on the index, so pinning the id list is exact, not an approximation.
public sealed record QueryHashRegeneration(
    IReadOnlyList<QueryHashWitnessEntry> Entries,
    IReadOnlyList<string> MissingArtworkIds);

/// Builds a `QueryHashWitnessDocument` from a real, already-loaded index
/// and an external image cache. The only pixel-touching calls here are
/// `Cv2.ImRead` (JPEG decode) and the two public functions that ARE the
/// query path: `QueryTransform.Prepare` and `CardHasher.Hash` -- no third
/// transform, no orientation flip (see the document type's own doc
/// comment for why a flip is not needed here).
public static class QueryHashWitnessBuilder
{
    /// Draws a NEW seeded sample from `index` and hashes it. Used only to
    /// GENERATE a witness (the `lab query-hash-witness` command) or to
    /// check the generator's own run-to-run determinism
    /// (`Build_CalledTwiceWithTheSameInputs_ProducesTheIdenticalDocument`).
    /// Never use this to reproduce an EXISTING committed witness for
    /// comparison -- see `BuildForArtworkIds`'s doc comment and
    /// `QueryHashRegeneration`'s.
    public static QueryHashWitnessDocument Build(
        HashIndexData index, string cacheDir, int sampleSize, int seed, string indexSha256, DateTimeOffset measuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cacheDir);
        ArgumentNullException.ThrowIfNull(indexSha256);

        var sample = RoundTripSampler.SelectUniformSample(index.Entries, sampleSize, seed);
        var regen = BuildForArtworkIds(cacheDir, sample.Select(e => e.ArtworkId).ToList());

        if (regen.MissingArtworkIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"QueryHashWitnessBuilder: {regen.MissingArtworkIds.Count} of {sample.Count} sampled artwork(s) had no usable " +
                $"cache image (first few: {string.Join(", ", regen.MissingArtworkIds.Take(5))}). The witness is a small, fixed " +
                "sample -- it must be built against a complete cache, not a partial one.");
        }

        var sorted = regen.Entries.OrderBy(e => e.ArtworkId, StringComparer.Ordinal).ToList();

        return new QueryHashWitnessDocument(
            ArchitectureProvenance.CurrentToken(),
            ArchitectureProvenance.CurrentDetail(),
            indexSha256,
            sampleSize,
            seed,
            measuredAtUtc,
            sorted);
    }

    /// Regenerates query-side hashes for EXACTLY the given `artworkIds`,
    /// in no particular order -- no sampling, no dependency on any index.
    /// An id whose cache render is missing (or fails to decode) is
    /// reported in `MissingArtworkIds`, never silently dropped and never
    /// folded into "differing": a missing render and a bit-different hash
    /// are different failure modes with different causes (an incomplete
    /// cache vs. an actual cross-architecture/regression divergence), and
    /// conflating them is exactly the defect this type exists to avoid
    /// (see `QueryHashWitnessDiff`'s own doc comment).
    public static QueryHashRegeneration BuildForArtworkIds(string cacheDir, IReadOnlyList<string> artworkIds)
    {
        ArgumentNullException.ThrowIfNull(cacheDir);
        ArgumentNullException.ThrowIfNull(artworkIds);

        var missing = new List<string>();
        var entries = new List<QueryHashWitnessEntry>(artworkIds.Count);

        foreach (var artworkId in artworkIds)
        {
            var imagePath = ImageCache.GetImagePath(cacheDir, artworkId);
            if (!File.Exists(imagePath))
            {
                missing.Add(artworkId);
                continue;
            }

            using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (color.Empty())
            {
                missing.Add(artworkId);
                continue;
            }

            var card = CardImageLoader.ToRectifiedCard(color);
            using var gray = QueryTransform.Prepare(card);
            var hash = CardHasher.Hash(gray);
            entries.Add(new QueryHashWitnessEntry(artworkId, hash.ToString()));
        }

        return new QueryHashRegeneration(entries, missing);
    }

    /// Bit-difference summary between a committed witness and a fresh
    /// regeneration drawn from the SAME `ArtworkId` list (via
    /// `BuildForArtworkIds(cacheDir, expected.Entries.Select(e =>
    /// e.ArtworkId))` -- never a re-sample) -- used both by the "same
    /// architecture" exact-equality assertion (any missing id or any
    /// differing hash there is a real regression) and by the "foreign
    /// architecture" informational skip (a non-empty `Differences` there
    /// is the whole point of this witness's existence). Missing and
    /// differing are reported SEPARATELY and never merged: a missing
    /// render means "not compared" (an incomplete cache), a difference
    /// means "compared and diverged" (the actual measurement) -- collapsing
    /// the two is the defect this overload exists to avoid (an index
    /// rebuild that drops an artwork must read as "N missing", not as "N
    /// hashes differ").
    public static QueryHashWitnessDiff Compare(QueryHashWitnessDocument expected, QueryHashRegeneration actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var actualByArtworkId = actual.Entries.ToDictionary(e => e.ArtworkId, e => e.QueryHashHex, StringComparer.Ordinal);
        return CompareCore(expected.Entries, actualByArtworkId, actual.MissingArtworkIds);
    }

    /// Document-to-document comparison, for the two callers that already
    /// hold two full `QueryHashWitnessDocument`s covering the SAME
    /// artworks (`Build` called twice with identical inputs -- the
    /// generator's own determinism check). Any `expected` id absent from
    /// `actual` is still reported as MISSING, not as a difference, for the
    /// same reason `Compare(QueryHashWitnessDocument, QueryHashRegeneration)`
    /// does -- but this overload should never be used to compare a
    /// committed witness against a re-SAMPLED document, which is the
    /// defect `BuildForArtworkIds`/`QueryHashRegeneration` exist to rule
    /// out entirely by construction.
    public static QueryHashWitnessDiff Compare(QueryHashWitnessDocument expected, QueryHashWitnessDocument actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var actualByArtworkId = actual.Entries.ToDictionary(e => e.ArtworkId, e => e.QueryHashHex, StringComparer.Ordinal);
        return CompareCore(expected.Entries, actualByArtworkId, []);
    }

    private static QueryHashWitnessDiff CompareCore(
        IReadOnlyList<QueryHashWitnessEntry> expectedEntries,
        IReadOnlyDictionary<string, string> actualByArtworkId,
        IReadOnlyList<string> explicitlyMissing)
    {
        var missing = new List<string>(explicitlyMissing);
        var differences = new List<QueryHashWitnessDifference>();

        foreach (var expectedEntry in expectedEntries)
        {
            if (missing.Contains(expectedEntry.ArtworkId, StringComparer.Ordinal))
            {
                continue;
            }

            if (!actualByArtworkId.TryGetValue(expectedEntry.ArtworkId, out var actualHex))
            {
                missing.Add(expectedEntry.ArtworkId);
                continue;
            }

            if (!string.Equals(expectedEntry.QueryHashHex, actualHex, StringComparison.Ordinal))
            {
                var bitDistance = HexHammingDistance(expectedEntry.QueryHashHex, actualHex);
                differences.Add(new QueryHashWitnessDifference(expectedEntry.ArtworkId, expectedEntry.QueryHashHex, actualHex, bitDistance));
            }
        }

        return new QueryHashWitnessDiff(expectedEntries.Count, missing, differences);
    }

    /// Hamming distance between two equal-length lowercase-hex hash
    /// strings, computed nibble by nibble via `PopCount` on the XOR of
    /// each pair -- deliberately independent of `CardHash`/`CardHasher` (a
    /// witness comparison must not depend on the very types whose
    /// cross-architecture behaviour it exists to check).
    private static int HexHammingDistance(string a, string b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Hash strings differ in length: {a.Length} vs {b.Length}.");
        }

        var distance = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var nibbleA = Convert.ToByte(a[i].ToString(), 16);
            var nibbleB = Convert.ToByte(b[i].ToString(), 16);
            distance += System.Numerics.BitOperations.PopCount((uint)(nibbleA ^ nibbleB));
        }

        return distance;
    }
}

public sealed record QueryHashWitnessDifference(string ArtworkId, string ExpectedHex, string ActualHex, int BitDistance);

/// `MissingArtworkIds` (render not found/decodable in the cache -- not
/// compared) and `Differences` (compared, hashes did not match) are
/// reported SEPARATELY and must never be summed or conflated -- see
/// `QueryHashWitnessBuilder.Compare`'s own doc comment for why. Bit-level
/// stats (`TotalDifferingBits`, `MaxBitsInOneHash`,
/// `AverageBitDistanceAmongDifferences`) are computed over `Differences`
/// ONLY -- a missing render has no bit distance to report.
public sealed record QueryHashWitnessDiff(
    int TotalCompared,
    IReadOnlyList<string> MissingArtworkIds,
    IReadOnlyList<QueryHashWitnessDifference> Differences)
{
    public int MissingCount => MissingArtworkIds.Count;

    public int DifferingCount => Differences.Count;

    public int TotalDifferingBits => Differences.Sum(d => d.BitDistance);

    public int MaxBitsInOneHash => Differences.Count == 0 ? 0 : Differences.Max(d => d.BitDistance);

    public double AverageBitDistanceAmongDifferences =>
        Differences.Count == 0 ? 0 : Differences.Average(d => (double)d.BitDistance);
}
