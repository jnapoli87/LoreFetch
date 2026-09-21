using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubOracleCatalogTests
{
    private static readonly string[] HostileNames =
    {
        "Kongming, \"Sleeping Dragon\"",
        "\"Rumors of My Death . . .\"",
        "Lim-Dûl's Vault",
        "Borrowing 100,000 Arrows",
        "+2 Mace",
    };

    [Fact]
    public void Default_Has33000Entries()
    {
        var catalog = new StubOracleCatalog();

        Assert.Equal(33_000, catalog.All.Count);
    }

    [Fact]
    public void ConfiguredCount_IsHonoured()
    {
        var catalog = new StubOracleCatalog(entryCount: 500);

        Assert.Equal(500, catalog.All.Count);
    }

    [Fact]
    public void Default_ContainsAllFiveHostileNamesVerbatim()
    {
        var catalog = new StubOracleCatalog();
        var names = catalog.All.Select(e => e.OracleName).ToHashSet();

        foreach (var hostile in HostileNames)
        {
            Assert.Contains(hostile, names);
        }
    }

    [Fact]
    public void ConfiguredCountSmallerThanFive_StillContainsAllFiveHostileNames()
    {
        var catalog = new StubOracleCatalog(entryCount: 2);
        var names = catalog.All.Select(e => e.OracleName).ToHashSet();

        foreach (var hostile in HostileNames)
        {
            Assert.Contains(hostile, names);
        }
    }

    [Fact]
    public void ZeroConfiguredCount_StillContainsAllFiveHostileNames()
    {
        var catalog = new StubOracleCatalog(entryCount: 0);

        Assert.Equal(5, catalog.All.Count);
        var names = catalog.All.Select(e => e.OracleName).ToHashSet();
        foreach (var hostile in HostileNames)
        {
            Assert.Contains(hostile, names);
        }
    }

    [Fact]
    public void AllOracleIds_AreDistinct()
    {
        var catalog = new StubOracleCatalog(entryCount: 10_000);

        var distinctCount = catalog.All.Select(e => e.OracleId).Distinct().Count();

        Assert.Equal(catalog.All.Count, distinctCount);
    }

    [Fact]
    public void NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StubOracleCatalog(entryCount: -1));
    }
}
