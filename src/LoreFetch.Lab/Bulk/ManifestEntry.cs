using System.Text;
using System.Text.Json;

namespace LoreFetch.Lab.Bulk;

/// The `bulk` command's output row -- everything B4b (image download) and
/// B4c (index build) need per surviving art, without either of them
/// having to re-run the filter cascade or re-parse Scryfall JSON.
public sealed record ManifestEntry(
    string ArtworkId,
    string OracleId,
    string OracleName,
    string TypeLine,
    string ImageUriNormal,
    bool IsBasicLand)
{
    /// Basic lands only, by the exact rule the accuracy doc calls out
    /// (`CLAUDE.md` "Lands are a smoke test, not a benchmark"): the
    /// type line must *start with* "Basic Land", not merely mention
    /// "Land" anywhere -- a card like a plain "Legendary Land" is not a
    /// basic, and `Contains("Land")` would wrongly call it one.
    public static bool IsBasicLandTypeLine(string typeLine) =>
        typeLine.StartsWith("Basic Land", StringComparison.Ordinal);

    public static ManifestEntry FromArtwork(RawArtwork artwork) => new(
        ArtworkId: artwork.Id,
        OracleId: artwork.OracleId,
        OracleName: artwork.Name,
        TypeLine: artwork.TypeLine,
        ImageUriNormal: artwork.ImageUriNormal
            ?? throw new ArgumentException(
                $"Artwork {artwork.Id} has no image_uris.normal -- it should have been " +
                $"dropped by {nameof(ArtworkFilterCascade)} before reaching the manifest.",
                nameof(artwork)),
        IsBasicLand: IsBasicLandTypeLine(artwork.TypeLine));
}

/// Reader/writer for the `bulk` command's manifest file.
///
/// JSONL (one JSON object per line), not CSV: oracle and card names
/// routinely contain commas, quotes and non-ASCII characters (see
/// `HashIndexFileTests`'s "Lim-Dûl's Vault"), and this file exists purely
/// for B4b/B4c to consume programmatically -- nobody hand-edits it, so
/// CSV's human-readability advantage buys nothing here while its quoting
/// rules are exactly the kind of thing that silently mis-parses a name.
public static class FilteredArtworkManifest
{
    public static void Write(string path, IEnumerable<ManifestEntry> entries)
    {
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var entry in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry));
        }
    }

    public static IReadOnlyList<ManifestEntry> Read(string path)
    {
        var result = new List<ManifestEntry>();

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<ManifestEntry>(line)
                ?? throw new FormatException($"Manifest line parsed to null: {line}");
            result.Add(entry);
        }

        return result;
    }
}
