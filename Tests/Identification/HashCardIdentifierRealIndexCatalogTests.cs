using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Lab;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// Package B8 item 6: `IOracleCatalog.All` (backing "Set card manually..."'s
/// type-ahead) loaded through `HashCardIdentifier` against the REAL
/// committed `data/index/cards.lfidx` -- not the small synthetic indexes
/// `HashCardIdentifierTests` builds by hand. Not artifact-gated (no
/// `RealCaptureGate`): the index is committed data, not a card-imagery
/// fixture, so it is present on every checkout and in CI -- a missing
/// index here is a real failure, not an expected skip.
public class HashCardIdentifierRealIndexCatalogTests
{
    private const int ExpectedDistinctOracleCount = 32_743;

    [Fact]
    public void All_RealCommittedIndex_HasExactlyOneEntryPerDistinctOracleIdInTheIndex()
    {
        Assert.True(RepoPaths.TryFindRepoRoot(out var repoRoot), "could not locate the repository root.");

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        Assert.True(File.Exists(indexPath), $"committed hash index not present at \"{indexPath}\".");

        var index = HashIndexFile.Read(indexPath);
        var identifier = HashCardIdentifier.Load(indexPath, NullLoggerFactory.Instance);

        var distinctOracleIdsInIndex = index.Entries.Select(e => e.OracleId).ToHashSet(StringComparer.Ordinal);

        // Pinned to this session's committed index (docs/accuracy.md
        // "Results -- win-x64 committed index rebuild"): 47,418 arts /
        // 32,743 oracle ids. A change here means the index was rebuilt --
        // update this constant alongside a fresh docs/accuracy.md entry,
        // not instead of one.
        Assert.Equal(ExpectedDistinctOracleCount, distinctOracleIdsInIndex.Count);

        // Exactly one catalog entry per distinct oracle id -- not more
        // (which would mean a reprint's second art leaked in as a second
        // row), not fewer (a dropped oracle).
        Assert.Equal(distinctOracleIdsInIndex.Count, identifier.All.Count);

        var catalogOracleIds = identifier.All.Select(e => e.OracleId).ToList();
        Assert.Equal(catalogOracleIds.Count, catalogOracleIds.Distinct(StringComparer.Ordinal).Count());

        // Every oracle id the index's own entries reference must resolve
        // through the catalog -- the type-ahead must never be missing a
        // card the identifier could actually return as a match.
        var catalogOracleIdSet = catalogOracleIds.ToHashSet(StringComparer.Ordinal);
        var missing = distinctOracleIdsInIndex.Where(id => !catalogOracleIdSet.Contains(id)).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} oracle id(s) from the index have no catalog entry, e.g. {string.Join(", ", missing.Take(5))}");

        // No entry with a blank name -- the type-ahead searches this text.
        var blankNamed = identifier.All.Where(e => string.IsNullOrWhiteSpace(e.OracleName)).ToList();
        Assert.True(blankNamed.Count == 0, $"{blankNamed.Count} catalog entr(y/ies) have a blank OracleName, e.g. OracleId {blankNamed.FirstOrDefault().OracleId}");
    }

    [Fact]
    public void All_RealCommittedIndex_CaseInsensitivePrefixSearchFindsKnownCards()
    {
        Assert.True(RepoPaths.TryFindRepoRoot(out var repoRoot), "could not locate the repository root.");

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        Assert.True(File.Exists(indexPath), $"committed hash index not present at \"{indexPath}\".");

        var identifier = HashCardIdentifier.Load(indexPath, NullLoggerFactory.Instance);

        // "Set card manually..." diagnostic (B8 item 6, reported to the
        // orchestrator for stream A's "does nothing" bug): a
        // case-insensitive PREFIX search over IOracleCatalog.All, exactly
        // the shape a type-ahead would run, must find these two real
        // cards. This does not touch stream A's own code -- it only
        // establishes whether the catalog itself would answer such a
        // search, ruling the catalog in or out as the cause.
        var freyaMatches = PrefixSearch(identifier, "Freya");
        var solRingMatches = PrefixSearch(identifier, "Sol Ring");

        Assert.True(freyaMatches.Count > 0, "no catalog entry's name starts with \"Freya\" (case-insensitive).");
        Assert.True(solRingMatches.Count > 0, "no catalog entry's name starts with \"Sol Ring\" (case-insensitive).");
    }

    private static List<OracleEntry> PrefixSearch(HashCardIdentifier identifier, string prefix) =>
        identifier.All
            .Where(e => e.OracleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
