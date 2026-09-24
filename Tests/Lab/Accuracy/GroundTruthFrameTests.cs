using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Accuracy;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

public class GroundTruthFrameTests
{
    [Fact]
    public void Resolve_NameInIndex_ResolvesOracleIdAndLandFlag()
    {
        var index = BuildIndex(
            ("oracle-forest", "Forest", true),
            ("oracle-solring", "Sol Ring", false));

        var rows = new[]
        {
            new GroundTruthRow("f1.png", 15, 1, 1, "Forest", "land", "light"),
            new GroundTruthRow("f2.png", 15, 1, 1, "Sol Ring", "normal", "light"),
        };

        var resolved = GroundTruthOracleLookup.Resolve(index, rows);

        Assert.Equal("oracle-forest", resolved[0].ExpectedOracleId);
        Assert.True(resolved[0].IsBasicLand);
        Assert.Equal("oracle-solring", resolved[1].ExpectedOracleId);
        Assert.False(resolved[1].IsBasicLand);
    }

    [Fact]
    public void Resolve_NameNotInIndex_ThrowsNamingTheRowAndCard()
    {
        var index = BuildIndex(("oracle-solring", "Sol Ring", false));
        var rows = new[] { new GroundTruthRow("f1.png", 15, 1, 1, "Not A Real Card", "normal", "light") };

        var ex = Assert.Throws<InvalidOperationException>(() => GroundTruthOracleLookup.Resolve(index, rows));
        Assert.Contains("Not A Real Card", ex.Message, StringComparison.Ordinal);
        Assert.Contains("f1.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupByFile_ValidNineSlotFrame_GroupsAndOrdersBySlot()
    {
        var index = BuildIndex(("o1", "Card One", false), ("o2", "Card Two", false), ("o3", "Card Three", false));
        var rows = new[]
        {
            new GroundTruthRow("f.png", 15, 3, 3, "Card Three", "normal", "light"),
            new GroundTruthRow("f.png", 15, 3, 1, "Card One", "normal", "light"),
            new GroundTruthRow("f.png", 15, 3, 2, "Card Two", "normal", "light"),
        };

        var resolved = GroundTruthOracleLookup.Resolve(index, rows);
        var frames = GroundTruthFrame.GroupByFile(resolved);

        Assert.Single(frames);
        Assert.Equal(3, frames[0].Slots.Count);
        Assert.Equal("Card One", frames[0].Slots[0].Row.OracleName);
        Assert.Equal("Card Two", frames[0].Slots[1].Row.OracleName);
        Assert.Equal("Card Three", frames[0].Slots[2].Row.OracleName);
    }

    [Fact]
    public void GroupByFile_SlotNumbersNotExactlyOneToLayout_Throws()
    {
        var index = BuildIndex(("o1", "Card One", false), ("o2", "Card Two", false));
        var rows = new[]
        {
            new GroundTruthRow("f.png", 15, 3, 1, "Card One", "normal", "light"),
            new GroundTruthRow("f.png", 15, 3, 5, "Card Two", "normal", "light"), // slot 5, not 2 or 3
        };

        var resolved = GroundTruthOracleLookup.Resolve(index, rows);
        Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthFrame.GroupByFile(resolved));
    }

    [Fact]
    public void GroupByFile_DisagreeingHeightOrLayoutUnderOneFile_Throws()
    {
        var index = BuildIndex(("o1", "Card One", false), ("o2", "Card Two", false));
        var rows = new[]
        {
            new GroundTruthRow("f.png", 15, 3, 1, "Card One", "normal", "light"),
            new GroundTruthRow("f.png", 20, 3, 2, "Card Two", "normal", "light"), // height disagrees
        };

        var resolved = GroundTruthOracleLookup.Resolve(index, rows);
        Assert.Throws<GroundTruthCsvFormatException>(() => GroundTruthFrame.GroupByFile(resolved));
    }

    private static HashIndexData BuildIndex(params (string OracleId, string OracleName, bool IsBasicLand)[] cards)
    {
        var entries = new List<HashIndexEntry>();
        var oracleTable = new List<OracleEntry>();
        foreach (var (oracleId, oracleName, isBasicLand) in cards)
        {
            entries.Add(new HashIndexEntry(default, oracleId, oracleName, $"art-{oracleId}", isBasicLand));
            oracleTable.Add(new OracleEntry(oracleId, oracleName));
        }

        return new HashIndexData(entries, oracleTable);
    }
}
