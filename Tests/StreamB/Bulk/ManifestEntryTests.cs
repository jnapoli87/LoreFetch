using LoreFetch.Lab.Bulk;
using Xunit;

namespace LoreFetch.Tests.StreamB.Bulk;

public class ManifestEntryTests
{
    [Theory]
    [InlineData("Basic Land — Bogland", true)]
    [InlineData("Basic Land", true)]
    // The discriminating case: contains "Land" but does not START WITH
    // "Basic Land". A `Contains("Land")` implementation wrongly calls
    // this a basic land; chaos-tested against exactly that mutation.
    [InlineData("Legendary Land", false)]
    [InlineData("Creature — Human Wizard", false)]
    [InlineData("Land", false)]
    public void IsBasicLandTypeLine_MatchesOnlyTypeLinesStartingWithBasicLand(string typeLine, bool expected)
    {
        Assert.Equal(expected, ManifestEntry.IsBasicLandTypeLine(typeLine));
    }

    [Fact]
    public void FromArtwork_NoImageUriNormal_Throws()
    {
        var artwork = new RawArtwork(
            Id: "art-x",
            OracleId: "oracle-x",
            Name: "Whatever",
            TypeLine: "Creature — Whatever",
            Lang: "en",
            ImageStatus: "highres_scan",
            Layout: "normal",
            SetType: "expansion",
            Frame: "2015",
            ImageUriNormal: null,
            Digital: false);

        Assert.Throws<ArgumentException>(() => ManifestEntry.FromArtwork(artwork));
    }

    [Fact]
    public void RoundTrip_MixedEntries_PreservesEverythingExactly()
    {
        var entries = new List<ManifestEntry>
        {
            new("art-0001", "oracle-0001", "Glimmering Sparrowhawk", "Creature — Bird",
                "https://example.invalid/1.jpg", IsBasicLand: false),
            new("art-0009", "oracle-0009", "Bogland", "Basic Land — Bogland",
                "https://example.invalid/9.jpg", IsBasicLand: true),
            // Non-ASCII name, same reasoning as HashIndexFileTests: an
            // encoding bug can corrupt exactly the characters a
            // round-trip over ASCII-only names would never exercise.
            new("art-0011", "oracle-0011", "Lim-Dûl's Vault", "Sorcery",
                "https://example.invalid/11.jpg", IsBasicLand: false),
        };

        var path = TempPath();
        try
        {
            FilteredArtworkManifest.Write(path, entries);
            var readBack = FilteredArtworkManifest.Read(path);

            Assert.Equal(entries.Count, readBack.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                Assert.Equal(entries[i], readBack[i]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_EmptyManifest_Succeeds()
    {
        var path = TempPath();
        try
        {
            FilteredArtworkManifest.Write(path, Array.Empty<ManifestEntry>());
            var readBack = FilteredArtworkManifest.Read(path);

            Assert.Empty(readBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"lorefetch-manifest-test-{Guid.NewGuid():N}.jsonl");
}
