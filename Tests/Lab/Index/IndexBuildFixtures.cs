using LoreFetch.Core.Abstractions;
using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Images;
using LoreFetch.Tests.Lab;
using OpenCvSharp;

namespace LoreFetch.Tests.Lab.Index;

/// Builds a throwaway on-disk cache directory (real JPEG files, via
/// `ImageCache`'s own path mapping so `IndexBuilder` finds them exactly the
/// way it would find real Scryfall downloads) plus a matching in-memory
/// manifest, for `IndexBuilderTests`/`BuildIndexCommandTests`. Deliberately
/// procedural, never a checked-in image -- DECISIONS.md "never commit card
/// imagery" applies to fixtures too, not just real Scryfall renders.
internal static class IndexBuildFixtures
{
    /// `count` entries, alternating between the canonical 488x680 size (so
    /// most of the built index reports zero "unexpected size") and a
    /// deliberately different size every fourth entry (so
    /// `UnexpectedSizeCount` has something real to count) -- and alternating
    /// between a small, fast-to-hash image and a much larger, slower one,
    /// so a `Parallel.For` run genuinely has entries finishing in a
    /// different order than they were started, which is what makes the
    /// "gather in completion order" mutation actually fail the ordering
    /// test rather than passing it by accident.
    public static (IReadOnlyList<ManifestEntry> Entries, string CacheDir) CreateCacheAndManifest(
        string cacheDir, int count, int seed = 1)
    {
        Directory.CreateDirectory(cacheDir);
        var entries = new List<ManifestEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var artworkId = $"art-{i:D4}";
            var oracleId = $"oracle-{i % Math.Max(1, count / 2):D4}"; // some oracle ids repeat, most don't
            var isUnexpectedSize = i % 4 == 3;
            var (width, height) = isUnexpectedSize
                ? (244, 340) // half the canonical size -- still decodes fine, just not 488x680
                : (RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight);

            using var mat = i % 3 == 0
                ? SyntheticImages.MakeHighFrequencyNoiseBgr(width, height, seed + i)
                : SyntheticImages.MakeCardLikeBgr(width, height, seed + i);

            var path = ImageCache.GetImagePath(cacheDir, artworkId);
            Cv2.ImWrite(path, mat);

            entries.Add(new ManifestEntry(
                ArtworkId: artworkId,
                OracleId: oracleId,
                OracleName: $"Test Card {i % Math.Max(1, count / 2)}",
                TypeLine: i == 0 ? "Basic Land — Forest" : "Creature — Test",
                ImageUriNormal: $"https://cards.scryfall.io/normal/front/0/0/{artworkId}.jpg",
                IsBasicLand: i == 0));
        }

        return (entries, cacheDir);
    }

    public static string NewTempCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lorefetch-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only -- never fail a test's teardown.
        }
    }
}
