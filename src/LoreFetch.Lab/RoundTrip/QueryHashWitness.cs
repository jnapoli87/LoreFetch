using System.Text.Json;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
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

/// Builds a `QueryHashWitnessDocument` from a real, already-loaded index
/// and an external image cache. The only pixel-touching calls here are
/// `Cv2.ImRead` (JPEG decode) and the two public functions that ARE the
/// query path: `QueryTransform.Prepare` and `CardHasher.Hash` -- no third
/// transform, no orientation flip (see the document type's own doc
/// comment for why a flip is not needed here).
public static class QueryHashWitnessBuilder
{
    public static QueryHashWitnessDocument Build(
        HashIndexData index, string cacheDir, int sampleSize, int seed, string indexSha256, DateTimeOffset measuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cacheDir);
        ArgumentNullException.ThrowIfNull(indexSha256);

        var sample = RoundTripSampler.SelectUniformSample(index.Entries, sampleSize, seed);
        var missing = new List<string>();
        var entries = new List<QueryHashWitnessEntry>(sample.Count);

        foreach (var entry in sample)
        {
            var imagePath = ImageCache.GetImagePath(cacheDir, entry.ArtworkId);
            if (!File.Exists(imagePath))
            {
                missing.Add(entry.ArtworkId);
                continue;
            }

            using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (color.Empty())
            {
                missing.Add(entry.ArtworkId);
                continue;
            }

            var card = CardImageLoader.ToRectifiedCard(color);
            using var gray = QueryTransform.Prepare(card);
            var hash = CardHasher.Hash(gray);
            entries.Add(new QueryHashWitnessEntry(entry.ArtworkId, hash.ToString()));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"QueryHashWitnessBuilder: {missing.Count} of {sample.Count} sampled artwork(s) had no usable " +
                $"cache image (first few: {string.Join(", ", missing.Take(5))}). The witness is a small, fixed " +
                "sample -- it must be built against a complete cache, not a partial one.");
        }

        var sorted = entries.OrderBy(e => e.ArtworkId, StringComparer.Ordinal).ToList();

        return new QueryHashWitnessDocument(
            ArchitectureProvenance.CurrentToken(),
            ArchitectureProvenance.CurrentDetail(),
            indexSha256,
            sampleSize,
            seed,
            measuredAtUtc,
            sorted);
    }

    /// Bit-difference summary between two witnesses covering the SAME
    /// artworks in the SAME order (callers should compare a freshly-built
    /// witness against a committed one drawn with the same seed/size) --
    /// used both by the "same architecture" exact-equality assertion (an
    /// unexpected non-empty result there is a real regression) and by the
    /// "foreign architecture" informational skip (a non-empty result there
    /// is the whole point of this witness's existence).
    public static QueryHashWitnessDiff Compare(QueryHashWitnessDocument expected, QueryHashWitnessDocument actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var actualByArtworkId = actual.Entries.ToDictionary(e => e.ArtworkId, e => e.QueryHashHex, StringComparer.Ordinal);
        var differences = new List<QueryHashWitnessDifference>();

        foreach (var expectedEntry in expected.Entries)
        {
            if (!actualByArtworkId.TryGetValue(expectedEntry.ArtworkId, out var actualHex))
            {
                differences.Add(new QueryHashWitnessDifference(expectedEntry.ArtworkId, expectedEntry.QueryHashHex, null, -1));
                continue;
            }

            if (!string.Equals(expectedEntry.QueryHashHex, actualHex, StringComparison.Ordinal))
            {
                var bitDistance = HexHammingDistance(expectedEntry.QueryHashHex, actualHex);
                differences.Add(new QueryHashWitnessDifference(expectedEntry.ArtworkId, expectedEntry.QueryHashHex, actualHex, bitDistance));
            }
        }

        return new QueryHashWitnessDiff(expected.Entries.Count, differences);
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

public sealed record QueryHashWitnessDifference(string ArtworkId, string ExpectedHex, string? ActualHex, int BitDistance);

public sealed record QueryHashWitnessDiff(int TotalCompared, IReadOnlyList<QueryHashWitnessDifference> Differences)
{
    public int DifferingCount => Differences.Count;

    public double AverageBitDistanceAmongDifferences =>
        Differences.Where(d => d.BitDistance >= 0).Select(d => (double)d.BitDistance).DefaultIfEmpty(0).Average();
}
