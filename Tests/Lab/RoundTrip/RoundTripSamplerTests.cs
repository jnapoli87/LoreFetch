using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.RoundTrip;
using Xunit;

namespace LoreFetch.Tests.Lab.RoundTrip;

/// Pure logic, no cache and no real index needed -- runs unconditionally
/// on every CI leg. Covers the property the round-trip gate's
/// reproducibility depends on: the SAME seed against the SAME entries
/// picks the SAME sample, every time, on any machine.
public class RoundTripSamplerTests
{
    [Fact]
    public void SelectStratifiedSample_SameSeedSameEntries_ProducesTheIdenticalSequence()
    {
        var entries = BuildEntries(landCount: 50, nonLandCount: 200);

        var first = RoundTripSampler.SelectStratifiedSample(entries, landCount: 10, nonLandCount: 20, seed: 42);
        var second = RoundTripSampler.SelectStratifiedSample(entries, landCount: 10, nonLandCount: 20, seed: 42);

        Assert.Equal(first.Select(e => e.ArtworkId), second.Select(e => e.ArtworkId));
    }

    [Fact]
    public void SelectStratifiedSample_DifferentSeed_ProducesADifferentSequence()
    {
        var entries = BuildEntries(landCount: 50, nonLandCount: 200);

        var first = RoundTripSampler.SelectStratifiedSample(entries, landCount: 10, nonLandCount: 20, seed: 42);
        var second = RoundTripSampler.SelectStratifiedSample(entries, landCount: 10, nonLandCount: 20, seed: 43);

        Assert.NotEqual(first.Select(e => e.ArtworkId), second.Select(e => e.ArtworkId));
    }

    [Fact]
    public void SelectStratifiedSample_ReturnsExactlyTheRequestedCounts_LandsFirstThenNonLands()
    {
        var entries = BuildEntries(landCount: 50, nonLandCount: 200);

        var sample = RoundTripSampler.SelectStratifiedSample(entries, landCount: 10, nonLandCount: 20, seed: 1);

        Assert.Equal(30, sample.Count);
        Assert.Equal(10, sample.Take(10).Count(e => e.IsBasicLand));
        Assert.Equal(0, sample.Take(10).Count(e => !e.IsBasicLand));
        Assert.Equal(20, sample.Skip(10).Count(e => !e.IsBasicLand));
        Assert.Equal(0, sample.Skip(10).Count(e => e.IsBasicLand));
    }

    [Fact]
    public void SelectStratifiedSample_NeverPicksTheSameEntryTwice()
    {
        var entries = BuildEntries(landCount: 50, nonLandCount: 200);

        var sample = RoundTripSampler.SelectStratifiedSample(entries, landCount: 20, nonLandCount: 180, seed: 7);

        Assert.Equal(sample.Count, sample.Select(e => e.ArtworkId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SelectStratifiedSample_RequestingMoreThanAvailable_ClampsRatherThanThrows()
    {
        var entries = BuildEntries(landCount: 3, nonLandCount: 5);

        var sample = RoundTripSampler.SelectStratifiedSample(entries, landCount: 100, nonLandCount: 100, seed: 1);

        Assert.Equal(3, sample.Count(e => e.IsBasicLand));
        Assert.Equal(5, sample.Count(e => !e.IsBasicLand));
    }

    [Fact]
    public void SelectUniformSample_SameSeedSameEntries_ProducesTheIdenticalSequence()
    {
        var entries = BuildEntries(landCount: 50, nonLandCount: 200);

        var first = RoundTripSampler.SelectUniformSample(entries, count: 50, seed: 99);
        var second = RoundTripSampler.SelectUniformSample(entries, count: 50, seed: 99);

        Assert.Equal(first.Select(e => e.ArtworkId), second.Select(e => e.ArtworkId));
        Assert.Equal(50, first.Count);
    }

    private static List<HashIndexEntry> BuildEntries(int landCount, int nonLandCount)
    {
        var entries = new List<HashIndexEntry>();
        var hash = new CardHash(new ulong[CardHash.WordCount]);

        for (var i = 0; i < landCount; i++)
        {
            entries.Add(new HashIndexEntry(hash, $"land-oracle-{i}", "Forest", $"land-art-{i:D4}", IsBasicLand: true));
        }

        for (var i = 0; i < nonLandCount; i++)
        {
            entries.Add(new HashIndexEntry(hash, $"nonland-oracle-{i}", $"Card {i}", $"nonland-art-{i:D4}", IsBasicLand: false));
        }

        return entries;
    }
}
