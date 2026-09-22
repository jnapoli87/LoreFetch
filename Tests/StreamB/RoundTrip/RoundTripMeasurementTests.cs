using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.RoundTrip;
using LoreFetch.Tests.StreamB;
using Xunit;

namespace LoreFetch.Tests.StreamB.RoundTrip;

/// Synthetic (no cache, no real index -- runs unconditionally on every CI
/// leg) tests of `RoundTripMeasurement` itself, built around a single
/// engineered fixture: two entries sharing ONE `OracleId` but carrying
/// DIFFERENT `ArtworkId`s, where the "sibling" art's hash is deliberately
/// closer to the query than the queried artwork's own entry. This is the
/// exact silent-failure shape CLAUDE.md's gate exists to catch ("a gate
/// asserting only OracleId passes while matching a different art of the
/// same card"), and it is the vehicle for this package's two chaos-test
/// requirements: proving `ArtworkId` catches what `OracleId` alone would
/// miss (brief-mandated), and proving `RoundTripMeasurement`'s margin scan
/// would silently break if its exclusion were ever loosened from
/// per-ARTWORK to per-ORACLE (this package's own chaos case).
public class RoundTripMeasurementTests
{
    /// The query and its OWN hash are built from the REAL query path
    /// (`QueryTransform.Prepare` -> `CardHasher.Hash`) against a
    /// deterministic synthetic card image -- never a real Scryfall render,
    /// so this needs no cache and carries no imagery risk. The two index
    /// entries are hand-built at deliberately known distances FROM that
    /// real hash:
    ///   - `siblingHash`: 2 bits flipped from the query's own upright hash
    ///     -- trivially close.
    ///   - `ownHash`: the ALL-ONES hash. Its Hamming distance to ANY hash
    ///     `h` is exactly `1024 - popcount(h)` -- for a real, varied
    ///     synthetic card image (`SyntheticImages.MakeCardLikeBgr` mixes a
    ///     brightness gradient with per-pixel noise, so its hash bits are
    ///     not lopsided toward all-0 or all-1), that lands in the low
    ///     hundreds at minimum, regardless of orientation -- so
    ///     `siblingHash` wins under BOTH the upright and the 180-degree-
    ///     flipped query hash `HashCardIdentifier.Identify` actually
    ///     tries, with no dependence on which orientation happens to win.
    private static (RectifiedCard Card, HashIndexData Index, HashIndexEntry OwnEntry, HashIndexEntry SiblingEntry) BuildSiblingArtFixture()
    {
        using var bgr = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 501);
        var card = SyntheticImages.ToRectifiedCard(bgr);

        using var gray = QueryTransform.Prepare(card);
        var queryHash = CardHasher.Hash(gray);

        var allOnesWords = Enumerable.Repeat(ulong.MaxValue, CardHash.WordCount).ToArray();
        var ownHash = new CardHash(allOnesWords);
        var siblingHash = HashFixtures.FlipBits(queryHash, 0, 1);

        const string sharedOracleId = "shared-oracle";
        const string sharedOracleName = "Shared Card";
        var ownEntry = new HashIndexEntry(ownHash, sharedOracleId, sharedOracleName, "own-art-id", IsBasicLand: false);
        var siblingEntry = new HashIndexEntry(siblingHash, sharedOracleId, sharedOracleName, "sibling-art-id", IsBasicLand: false);

        var index = new HashIndexData(
            entries: [ownEntry, siblingEntry],
            oracleTable: [new OracleEntry(sharedOracleId, sharedOracleName)]);

        return (card, index, ownEntry, siblingEntry);
    }

    /// The brief-mandated chaos case: build the OracleId-only assertion a
    /// weaker gate would have used, and show it PASSES on this fixture
    /// while the real ArtworkId assertion FAILS -- i.e. the OracleId-only
    /// check is strictly weaker, and this fixture is exactly the case that
    /// tells them apart.
    [Fact]
    public void Measure_SiblingArtCloserThanOwnArt_ArtworkIdCatchesIt_OracleIdAloneWouldNotHave()
    {
        var (card, index, ownEntry, siblingEntry) = BuildSiblingArtFixture();
        var identifier = new HashCardIdentifier(index);

        var outcome = RoundTripMeasurement.Measure(identifier, index, card, ownEntry, maxCandidates: 1);

        // The weaker gate: "rank 1 named the right ORACLE" -- passes, because
        // both entries in this fixture share one oracle and Identify only
        // ever returns that one oracle here.
        Assert.Equal(ownEntry.OracleId, outcome.Rank1OracleId);

        // The real gate: "rank 1 named the right ARTWORK" -- fails, because
        // Identify's own best-per-oracle collapse picked the SIBLING art,
        // not the artwork this measurement actually queried with.
        Assert.Equal(siblingEntry.ArtworkId, outcome.Rank1ArtworkId);
        Assert.NotEqual(ownEntry.ArtworkId, outcome.Rank1ArtworkId);
        Assert.False(outcome.IsCorrect);
    }

    /// This package's OWN required chaos case (the brief's "escape valve"):
    /// `RoundTripMeasurement`'s raw entry scan for the MARGIN must exclude
    /// only the queried artwork's OWN entry, not every entry sharing its
    /// OracleId -- otherwise a genuinely close SIBLING art (exactly the
    /// case above) would be invisible to the "best different artwork"
    /// search, and the reported margin would be silently inflated (or, as
    /// in this fixture with only two entries, `BestOtherArtworkId` would
    /// come back null and `BestOtherDistance` would stay `int.MaxValue`
    /// with nothing left to find). Chaos-verified: temporarily loosening
    /// `RoundTripMeasurement.Measure`'s exclusion from
    /// `entry.ArtworkId == queriedEntry.ArtworkId` to
    /// `entry.OracleId == queriedEntry.OracleId` makes this exact
    /// assertion fail -- see this package's implementation report.
    [Fact]
    public void Measure_BestOtherEntry_ExcludesOnlyTheQueriedArtwork_NotItsWholeOracle()
    {
        var (card, index, ownEntry, siblingEntry) = BuildSiblingArtFixture();
        var identifier = new HashCardIdentifier(index);

        var outcome = RoundTripMeasurement.Measure(identifier, index, card, ownEntry, maxCandidates: 1);

        // The sibling shares ownEntry's OracleId but is a DIFFERENT
        // artwork -- it must still be found as "the best other entry",
        // not excluded alongside ownEntry just because they share a card.
        Assert.Equal(siblingEntry.ArtworkId, outcome.BestOtherArtworkId);
        Assert.True(outcome.BestOtherDistance < 1000, "the sibling's distance must be the small, engineered value, not int.MaxValue's near-1024 stand-in.");
    }

    [Fact]
    public void Measure_QueriedArtworkNotInIndex_Throws()
    {
        var (card, index, _, _) = BuildSiblingArtFixture();
        var identifier = new HashCardIdentifier(index);
        var foreignEntry = new HashIndexEntry(index.Entries[0].Hash, "shared-oracle", "Shared Card", "not-in-the-index", IsBasicLand: false);

        Assert.Throws<InvalidOperationException>(() => RoundTripMeasurement.Measure(identifier, index, card, foreignEntry, maxCandidates: 1));
    }

    [Fact]
    public void MissingImageOutcome_IsNeverCorrect_AndCarriesNoMeasurement()
    {
        var entry = new HashIndexEntry(new CardHash(new ulong[CardHash.WordCount]), "o", "n", "a", IsBasicLand: false);

        var outcome = RoundTripMeasurement.MissingImageOutcome(entry);

        Assert.False(outcome.ImageAvailable);
        Assert.False(outcome.IsCorrect);
        Assert.Null(outcome.Rank1ArtworkId);
    }
}
