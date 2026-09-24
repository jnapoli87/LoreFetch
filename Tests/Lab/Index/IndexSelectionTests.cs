using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Index;
using Xunit;

namespace LoreFetch.Tests.Lab.Index;

public sealed class IndexSelectionTests
{
    private static ManifestEntry Entry(string artworkId, string oracleId = "") => new(
        ArtworkId: artworkId,
        OracleId: string.IsNullOrEmpty(oracleId) ? "oracle-" + artworkId : oracleId,
        OracleName: "Name " + artworkId,
        TypeLine: "Creature",
        ImageUriNormal: "https://cards.scryfall.io/normal/x.jpg",
        IsBasicLand: false);

    [Fact]
    public void Subset_TakesFirstNInManifestOrder()
    {
        var manifest = new[] { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        var subset = IndexSelection.Subset(manifest, 2);

        Assert.Equal(["a", "b"], subset.Select(e => e.ArtworkId));
    }

    [Fact]
    public void Subset_CountLargerThanManifest_ReturnsWholeManifest()
    {
        var manifest = new[] { Entry("a"), Entry("b") };

        var subset = IndexSelection.Subset(manifest, 10);

        Assert.Equal(2, subset.Count);
    }

    [Fact]
    public void Subset_ZeroCount_ReturnsEmpty()
    {
        var manifest = new[] { Entry("a"), Entry("b") };

        var subset = IndexSelection.Subset(manifest, 0);

        Assert.Empty(subset);
    }

    [Fact]
    public void Subset_NegativeCount_Throws()
    {
        var manifest = new[] { Entry("a") };

        Assert.Throws<ArgumentOutOfRangeException>(() => IndexSelection.Subset(manifest, -1));
    }

    [Fact]
    public void ByIds_ReturnsNamedEntriesInManifestOrder_NotFileOrder()
    {
        var manifest = new[] { Entry("a"), Entry("b"), Entry("c"), Entry("d") };

        // Requested out of manifest order -- "d" before "b".
        var selected = IndexSelection.ByIds(manifest, ["d", "b"], fill: 0, out var unmatched);

        Assert.Empty(unmatched);
        Assert.Equal(["b", "d"], selected.Select(e => e.ArtworkId));
    }

    [Fact]
    public void ByIds_UnmatchedId_IsReportedNotSilentlyDropped()
    {
        var manifest = new[] { Entry("a"), Entry("b") };

        var selected = IndexSelection.ByIds(manifest, ["a", "does-not-exist"], fill: 0, out var unmatched);

        Assert.Equal(["a"], selected.Select(e => e.ArtworkId));
        Assert.Equal(["does-not-exist"], unmatched);
    }

    [Fact]
    public void ByIds_Fill_AddsFirstUnselectedManifestEntries_InManifestOrder()
    {
        var manifest = new[] { Entry("a"), Entry("b"), Entry("c"), Entry("d"), Entry("e") };

        // "c" is named directly; fill should add "a" and "b" (the first two
        // manifest entries not already selected), not "d"/"e".
        var selected = IndexSelection.ByIds(manifest, ["c"], fill: 2, out var unmatched);

        Assert.Empty(unmatched);
        Assert.Equal(["a", "b", "c"], selected.Select(e => e.ArtworkId));
    }

    [Fact]
    public void ByIds_FillDoesNotDoubleCountAlreadyNamedEntries()
    {
        var manifest = new[] { Entry("a"), Entry("b"), Entry("c") };

        // "a" is named AND would be the first fill candidate -- fill must
        // not re-add it and inflate the result past what fill=1 promises.
        var selected = IndexSelection.ByIds(manifest, ["a"], fill: 1, out _);

        Assert.Equal(["a", "b"], selected.Select(e => e.ArtworkId));
    }

    [Fact]
    public void ByIds_NegativeFill_Throws()
    {
        var manifest = new[] { Entry("a") };

        Assert.Throws<ArgumentOutOfRangeException>(() => IndexSelection.ByIds(manifest, ["a"], fill: -1, out _));
    }
}
