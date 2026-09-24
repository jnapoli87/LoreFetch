using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// `HashCardIdentifier` tests (package B3b). Two fixture shapes recur:
///
/// - A POINT-SYMMETRIC query card (`SyntheticImages.MakePointSymmetricBgr`)
///   for every test that is NOT about orientation. Flipping a point-
///   symmetric image reproduces the identical Mat, so the query's upright
///   and 180-degree hashes come out bit-identical -- which means "distance
///   from a synthetic entry to the query" has exactly one value to reason
///   about, and that value is fully controlled by `HashFixtures.FlipBits`.
///   Without this, a ranking/distinctness/no-threshold test would need to
///   hand-derive what pixel values survive `CardHasher`'s crop, resize and
///   per-cell median just to know which of the two orientations an entry's
///   distance would be measured against.
/// - An ASYMMETRIC card, deliberately NOT flip-invariant, for the
///   orientation tests -- see `Orientation_*` below, where the whole point
///   is that upright and flipped hash to different values.
///
/// Chaos-tested per docs/TESTING.md "Standing practice" -- see the B3b
/// hand-off reports for the seven mutations exercised across this file's
/// history (dropped orientation search, no per-oracle dedup, worst-instead
/// of-best art, an early-rejection threshold, a truncated word sum, a
/// reversed tie-break, and the bounded top-K buffer comparing against its
/// own best slot instead of its worst) and their outcomes.
public class HashCardIdentifierTests
{
    private const int CardWidth = RectifiedCard.CanonicalWidth;
    private const int CardHeight = RectifiedCard.CanonicalHeight;

    // ---- Ranking ----------------------------------------------------

    [Fact]
    public void Identify_RanksByAscendingDistance_ExactOrderFromKnownBitFlips()
    {
        var baseHash = SymmetricQueryHash(seed: 1001);

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-d100", "Card D100", "art-d100", HashFixtures.FlipFirstNBits(baseHash, 100)),
            // Bits 1000/1001 fall in grid word 15 (bit/64 = 15) -- the LAST
            // of the 16 words, deliberately, not word 0 like the others.
            // Distance math that dropped the last word (a real chaos-tested
            // mutation) would silently under-count exactly this entry and
            // still pass a test that only ever flips bits in word 0.
            MakeEntry("oracle-d2", "Card D2", "art-d2", HashFixtures.FlipBits(baseHash, 1000, 1001)),
            MakeEntry("oracle-d30", "Card D30", "art-d30", HashFixtures.FlipFirstNBits(baseHash, 30)),
            MakeEntry("oracle-d5", "Card D5", "art-d5", HashFixtures.FlipFirstNBits(baseHash, 5)),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1001), maxCandidates: 4);

        Assert.Equal(4, result.Count);
        Assert.Equal(["oracle-d2", "oracle-d5", "oracle-d30", "oracle-d100"], result.Select(c => c.OracleId));
        Assert.Equal([2, 5, 30, 100], result.Select(c => c.Distance));
    }

    /// Direct, narrow companion to the ranking test above: proves every one
    /// of the 16 grid-cell words is actually summed into the distance, not
    /// just that the FIRST word is (which every bit-flip in the ranking
    /// test below 100 would satisfy on its own). Flips exactly one bit in
    /// each of the 16 words and asserts the distance is exactly 16 -- a
    /// distance computed over any fewer than all 16 words would undercount.
    [Fact]
    public void Identify_Distance_SumsEveryGridWord_NotJustTheFirst()
    {
        var baseHash = SymmetricQueryHash(seed: 1010);

        var oneBitPerWord = new int[CardHash.WordCount];
        for (var word = 0; word < CardHash.WordCount; word++)
        {
            oneBitPerWord[word] = (word * 64) + 3; // an arbitrary, distinct bit inside each word
        }

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-all-words", "Card All Words", "art-all-words", HashFixtures.FlipBits(baseHash, oneBitPerWord)),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1010), maxCandidates: 1);

        var candidate = Assert.Single(result);
        Assert.Equal(CardHash.WordCount, candidate.Distance);
    }

    /// Three oracles at the SAME distance -- a genuine tie the ranking test
    /// above never exercises, since its four entries all sit at distinct
    /// distances. Entries are listed in the order oracle-T0, oracle-T1,
    /// oracle-T2, so `HashFixtures.BuildIndexData`'s first-seen-order dedup
    /// gives them exactly that oracle-table index order. Asking for fewer
    /// candidates than there are tied oracles forces the tie-break to pick
    /// a winner: ascending oracle-table index keeps T0 and T1, not T2.
    [Fact]
    public void Identify_TiedDistances_TieBreaksByAscendingOracleTableIndex()
    {
        var baseHash = SymmetricQueryHash(seed: 1011);
        var tiedHash = HashFixtures.FlipFirstNBits(baseHash, 40);

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-T0", "Card T0", "art-T0", tiedHash),
            MakeEntry("oracle-T1", "Card T1", "art-T1", tiedHash),
            MakeEntry("oracle-T2", "Card T2", "art-T2", tiedHash),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1011), maxCandidates: 2);

        Assert.Equal(["oracle-T0", "oracle-T1"], result.Select(c => c.OracleId));
        Assert.Equal([40, 40], result.Select(c => c.Distance));
    }

    /// Targets a specific bug shape in the bounded top-K buffer: comparing
    /// a new candidate against the buffer's BEST kept slot instead of its
    /// WORST. Five oracles in table order at distances [100, 90, 80, 20,
    /// 50], `maxCandidates: 3`. The buffer fills with the first three
    /// (100, 90, 80) -- all "poor" relative to what arrives later. The
    /// fourth (20) correctly displaces the worst (100). The fifth (50) is
    /// worse than the buffer's best (20) but better than its current worst
    /// (90) -- a correct implementation displaces 90; a "compare against
    /// best" implementation rejects 50 outright, silently losing a
    /// candidate that belongs in the top 3.
    [Fact]
    public void Identify_MidQualityCandidateArrivingAfterBufferFills_DisplacesTheWorstKept_NotRejectedAgainstTheBest()
    {
        var baseHash = SymmetricQueryHash(seed: 1012);

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-A100", "Card A100", "art-A100", HashFixtures.FlipFirstNBits(baseHash, 100)),
            MakeEntry("oracle-B90", "Card B90", "art-B90", HashFixtures.FlipFirstNBits(baseHash, 90)),
            MakeEntry("oracle-C80", "Card C80", "art-C80", HashFixtures.FlipFirstNBits(baseHash, 80)),
            MakeEntry("oracle-D20", "Card D20", "art-D20", HashFixtures.FlipFirstNBits(baseHash, 20)),
            MakeEntry("oracle-E50", "Card E50", "art-E50", HashFixtures.FlipFirstNBits(baseHash, 50)),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1012), maxCandidates: 3);

        Assert.Equal(["oracle-D20", "oracle-E50", "oracle-C80"], result.Select(c => c.OracleId));
        Assert.Equal([20, 50, 80], result.Select(c => c.Distance));
    }

    /// Property test: `Identify` against a from-scratch naive reference for
    /// many random oracles, many arts per oracle (so dedup and "best art"
    /// selection are both exercised) and several `maxCandidates` values,
    /// including one bigger than the oracle count. The reference is
    /// deliberately NOT the production code path -- it recomputes best-
    /// per-oracle distance by brute force in the test and does a plain
    /// full sort by (Distance, OracleIndex) -- so this test only agrees
    /// with `HashCardIdentifier` if the bounded top-K selection actually
    /// picks the same set a full sort would. Random entries (not bit-flips
    /// off a known base) land near the ~512-bit binomial peak of a 1024-bit
    /// space, which concentrates a few hundred samples into a much smaller
    /// range of integer distances -- ties across DIFFERENT oracles are
    /// therefore common without needing to force them, exercising the tie-
    /// break at scale rather than only in the single hand-built case above.
    [Fact]
    public void Identify_MatchesNaiveFullSortReference_AcrossManyRandomOraclesAndCandidateCounts()
    {
        const int oracleCount = 300;
        const int maxArtsPerOracle = 4;
        var random = new Random(20260922);

        var queryHash = SymmetricQueryHash(seed: 20260922);
        var card = SymmetricQueryCard(seed: 20260922);

        var entries = new List<HashIndexEntry>();
        for (var oracleIndex = 0; oracleIndex < oracleCount; oracleIndex++)
        {
            var oracleId = $"oracle-{oracleIndex:D4}";
            var oracleName = $"Random Card {oracleIndex}";
            var artCount = random.Next(1, maxArtsPerOracle + 1);

            for (var art = 0; art < artCount; art++)
            {
                var words = new ulong[CardHash.WordCount];
                for (var w = 0; w < words.Length; w++)
                {
                    words[w] = unchecked((ulong)random.NextInt64());
                }

                entries.Add(new HashIndexEntry(
                    new CardHash(words), oracleId, oracleName, $"art-{oracleIndex:D4}-{art}", IsBasicLand: false));
            }
        }

        var identifier = BuildIdentifier(entries);

        // Naive reference, computed independently of HashCardIdentifier:
        // best (distance, artwork) per oracle by brute force, oracle-table
        // index by first-seen order (matching HashFixtures.BuildIndexData),
        // then a plain full sort.
        var bestByOracle = new Dictionary<string, (int Distance, string ArtworkId)>(StringComparer.Ordinal);
        var oracleTableIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!oracleTableIndex.ContainsKey(entry.OracleId))
            {
                oracleTableIndex[entry.OracleId] = oracleTableIndex.Count;
            }

            var distance = entry.Hash.HammingDistance(queryHash);
            if (!bestByOracle.TryGetValue(entry.OracleId, out var current) || distance < current.Distance)
            {
                bestByOracle[entry.OracleId] = (distance, entry.ArtworkId);
            }
        }

        var naiveRanked = bestByOracle
            .Select(kv => (OracleId: kv.Key, kv.Value.Distance, kv.Value.ArtworkId, OracleIndex: oracleTableIndex[kv.Key]))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.OracleIndex)
            .ToList();

        foreach (var maxCandidates in new[] { 1, 3, 9, oracleCount + 50 })
        {
            var expected = naiveRanked.Take(maxCandidates).ToList();
            var actual = identifier.Identify(card, maxCandidates);

            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.Select(x => x.OracleId), actual.Select(c => c.OracleId));
            Assert.Equal(expected.Select(x => x.Distance), actual.Select(c => c.Distance));
            Assert.Equal(expected.Select(x => x.ArtworkId), actual.Select(c => c.ArtworkId));
        }
    }

    // ---- Distinctness -------------------------------------------------

    [Fact]
    public void Identify_OracleWithManyArts_AppearsOnceAtItsBestArtsDistanceAndArtworkId()
    {
        var baseHash = SymmetricQueryHash(seed: 1002);

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-multi", "Reprinted Card", "art-worst", HashFixtures.FlipFirstNBits(baseHash, 80)),
            MakeEntry("oracle-multi", "Reprinted Card", "art-best", HashFixtures.FlipFirstNBits(baseHash, 10)),
            MakeEntry("oracle-multi", "Reprinted Card", "art-mid", HashFixtures.FlipFirstNBits(baseHash, 50)),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1002), maxCandidates: 5);

        var candidate = Assert.Single(result);
        Assert.Equal("oracle-multi", candidate.OracleId);
        Assert.Equal(10, candidate.Distance);
        Assert.Equal("art-best", candidate.ArtworkId);
    }

    [Fact]
    public void Identify_ReturnsMaxCandidatesDistinctOracles_WhenTheIndexHasAtLeastThatMany()
    {
        var baseHash = SymmetricQueryHash(seed: 1003);

        var entries = new List<HashIndexEntry>();
        for (var i = 0; i < 6; i++)
        {
            // distances 10, 20, 30, 40, 50, 60 -- distinct and known.
            entries.Add(MakeEntry($"oracle-{i}", $"Card {i}", $"art-{i}", HashFixtures.FlipFirstNBits(baseHash, 10 * (i + 1))));
        }

        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1003), maxCandidates: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal([10, 20, 30], result.Select(c => c.Distance));
        Assert.Equal(3, result.Select(c => c.OracleId).Distinct().Count());
    }

    [Fact]
    public void Identify_ReturnsFewerThanMaxCandidates_WhenTheIndexHasFewerOracleCards()
    {
        var baseHash = SymmetricQueryHash(seed: 1004);

        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-a", "Card A", "art-a", HashFixtures.FlipFirstNBits(baseHash, 10)),
            MakeEntry("oracle-b", "Card B", "art-b", HashFixtures.FlipFirstNBits(baseHash, 20)),
        };
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1004), maxCandidates: 10);

        Assert.Equal(2, result.Count);
    }

    // ---- No threshold ---------------------------------------------------

    [Fact]
    public void Identify_QueryFarFromEverything_StillReturnsMaxCandidates_NoThresholdFiltering()
    {
        var baseHash = SymmetricQueryHash(seed: 1005);

        // All well above CLAUDE.md's ~430-bit "indistinguishable from noise"
        // floor, and above any plausible good/ok threshold -- if Identify
        // ever grew an early-rejection cutoff (CLAUDE.md "do not port
        // upstream's early rejection"), some of these would be dropped.
        var distances = new[] { 400, 500, 600, 700, 800 };
        var entries = distances
            .Select((d, i) => MakeEntry($"oracle-far-{i}", $"Far Card {i}", $"art-far-{i}", HashFixtures.FlipFirstNBits(baseHash, d)))
            .ToList();
        var identifier = BuildIdentifier(entries);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1005), maxCandidates: 5);

        Assert.Equal(5, result.Count);
        Assert.Equal(distances, result.Select(c => c.Distance));
    }

    // ---- Empty index ----------------------------------------------------

    [Fact]
    public void Identify_EmptyIndex_ReturnsEmptyList()
    {
        var identifier = BuildIdentifier([]);

        var result = identifier.Identify(SymmetricQueryCard(seed: 1006), maxCandidates: 5);

        Assert.Empty(result);
    }

    [Fact]
    public void Identify_MaxCandidatesNotPositive_ThrowsArgumentOutOfRange()
    {
        var identifier = BuildIdentifier([MakeEntry("oracle-a", "Card A", "art-a", SymmetricQueryHash(seed: 1007))]);

        Assert.Throws<ArgumentOutOfRangeException>(() => identifier.Identify(SymmetricQueryCard(seed: 1007), maxCandidates: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => identifier.Identify(SymmetricQueryCard(seed: 1007), maxCandidates: -1));
    }

    // ---- Orientation ------------------------------------------------

    /// A card laid on the mat 180 degrees round rectifies to a valid quad
    /// (docs/design/identification.md "B5", "Card orientation is unhandled"),
    /// so a query built from an upside-down capture must still retrieve the
    /// index entry that was built from the SAME card the right way up.
    [Fact]
    public void Identify_UpsideDownCard_RetrievesSameArtworkId_AtDistanceZero()
    {
        using var uprightBgr = SyntheticImages.MakeCardLikeBgr(CardWidth, CardHeight, seed: 2001);
        var uprightCard = SyntheticImages.ToRectifiedCard(uprightBgr);
        using var uprightGray = QueryTransform.Prepare(uprightCard);
        var uprightHash = CardHasher.Hash(uprightGray);

        using var upsideDownBgr = new Mat();
        Cv2.Flip(uprightBgr, upsideDownBgr, FlipMode.XY);
        var upsideDownCard = SyntheticImages.ToRectifiedCard(upsideDownBgr);

        // The index holds only the UPRIGHT hash -- exactly what a real
        // index built from a Scryfall render would hold.
        var identifier = BuildIdentifier([MakeEntry("oracle-1", "Card One", "art-1", uprightHash)]);

        var result = identifier.Identify(upsideDownCard, maxCandidates: 1);

        var candidate = Assert.Single(result);
        Assert.Equal("oracle-1", candidate.OracleId);
        Assert.Equal("art-1", candidate.ArtworkId);
        // Bit-exact: flipping the query's grayscale twice (once because the
        // capture was upside down, once because Identify tries the 180
        // variant) reproduces the original upright grayscale byte for byte,
        // so the entry it was hashed from matches at distance 0, not merely
        // "small".
        Assert.Equal(0, candidate.Distance);
    }

    /// Proves the SEARCH tries both orientations, not just that an upside-
    /// down query happens to work: builds a card whose upright and 180-
    /// degree hashes are known to differ substantially, then plants the
    /// index entry at the FLIPPED hash. An upright query can only reach
    /// that entry at distance 0 if Identify actually hashes and searches
    /// the 180-degree variant too -- searching only the upright orientation
    /// would measure the large upright-vs-flipped distance instead.
    [Fact]
    public void Identify_AsymmetricCard_SearchesBothOrientations_FlippedHashWins()
    {
        using var bgr = SyntheticImages.MakeCardLikeBgr(CardWidth, CardHeight, seed: 2002);
        var uprightCard = SyntheticImages.ToRectifiedCard(bgr);
        using var uprightGray = QueryTransform.Prepare(uprightCard);
        var uprightHash = CardHasher.Hash(uprightGray);

        using var flippedGray = new Mat();
        Cv2.Flip(uprightGray, flippedGray, FlipMode.XY);
        var flippedHash = CardHasher.Hash(flippedGray);

        // Sanity check on the fixture itself: if upright and flipped landed
        // close together by chance, this test would prove nothing. A card-
        // like image (gradient + noise, not point-symmetric) should differ
        // substantially between the two orientations.
        var fixtureAsymmetry = uprightHash.HammingDistance(flippedHash);
        Assert.True(fixtureAsymmetry > 50, $"Fixture is not asymmetric enough to prove anything (distance {fixtureAsymmetry}).");

        // The index's only entry is planted at the FLIPPED hash -- as if
        // some reference art happened to equal this synthetic card rotated
        // 180 degrees.
        var identifier = BuildIdentifier([MakeEntry("oracle-2", "Card Two", "art-2", flippedHash)]);

        var result = identifier.Identify(uprightCard, maxCandidates: 1);

        var candidate = Assert.Single(result);
        Assert.Equal("oracle-2", candidate.OracleId);
        Assert.Equal(0, candidate.Distance);
    }

    // ---- Catalog -------------------------------------------------------

    [Fact]
    public void All_ReturnsEveryDistinctOracleExactlyOnce()
    {
        var baseHash = SymmetricQueryHash(seed: 1008);
        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-a", "Card A", "art-a1", HashFixtures.FlipFirstNBits(baseHash, 10)),
            MakeEntry("oracle-a", "Card A", "art-a2", HashFixtures.FlipFirstNBits(baseHash, 20)), // second art, same oracle
            MakeEntry("oracle-b", "Card B", "art-b1", HashFixtures.FlipFirstNBits(baseHash, 30)),
        };
        var identifier = BuildIdentifier(entries);

        Assert.Equal(
            [new OracleEntry("oracle-a", "Card A"), new OracleEntry("oracle-b", "Card B")],
            identifier.All);
    }

    // ---- Name ------------------------------------------------------------

    [Fact]
    public void Name_IsCardSpotterHashV1()
    {
        var identifier = BuildIdentifier([]);

        Assert.Equal("CardSpotterHash/v1", identifier.Name);
    }

    // ---- Load --------------------------------------------------------

    [Fact]
    public void Load_RoundTripsThroughHashIndexFile_AndIdentifiesCorrectly()
    {
        var baseHash = SymmetricQueryHash(seed: 1009);
        var entries = new List<HashIndexEntry>
        {
            MakeEntry("oracle-a", "Card A", "art-a", HashFixtures.FlipFirstNBits(baseHash, 15)),
            MakeEntry("oracle-b", "Card B", "art-b", HashFixtures.FlipFirstNBits(baseHash, 200)),
        };

        var path = Path.Combine(Path.GetTempPath(), $"lorefetch-b3b-test-{Guid.NewGuid():N}.lfidx");
        try
        {
            HashIndexFile.Write(path, entries);

            var identifier = HashCardIdentifier.Load(path, NullLoggerFactory.Instance);

            Assert.Equal(2, identifier.All.Count);

            var result = identifier.Identify(SymmetricQueryCard(seed: 1009), maxCandidates: 2);
            Assert.Equal(["oracle-a", "oracle-b"], result.Select(c => c.OracleId));
            Assert.Equal([15, 200], result.Select(c => c.Distance));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lorefetch-b3b-missing-{Guid.NewGuid():N}.lfidx");

        Assert.Throws<FileNotFoundException>(() => HashCardIdentifier.Load(path, NullLoggerFactory.Instance));
    }

    // ---- Fixture helpers --------------------------------------------

    private static HashCardIdentifier BuildIdentifier(IReadOnlyList<HashIndexEntry> entries) =>
        new(HashFixtures.BuildIndexData(entries));

    private static HashIndexEntry MakeEntry(string oracleId, string oracleName, string artworkId, CardHash hash) =>
        new(hash, oracleId, oracleName, artworkId, IsBasicLand: false);

    /// A point-symmetric card built for `seed` (see `SyntheticImages`'s own
    /// notes on why) -- the `RectifiedCard` form, for feeding to
    /// `Identify`.
    private static RectifiedCard SymmetricQueryCard(int seed)
    {
        using var bgr = SyntheticImages.MakePointSymmetricBgr(CardWidth, CardHeight, seed);
        return SyntheticImages.ToRectifiedCard(bgr);
    }

    /// The exact hash `Identify` will compute for `SymmetricQueryCard(seed)`
    /// -- for BOTH orientations, since a point-symmetric card's upright and
    /// flipped hashes are identical. Computed independently via the same
    /// public `QueryTransform`/`CardHasher` calls `HashCardIdentifier` uses
    /// internally, not by reaching into the type under test.
    private static CardHash SymmetricQueryHash(int seed)
    {
        var card = SymmetricQueryCard(seed);
        using var gray = QueryTransform.Prepare(card);
        return CardHasher.Hash(gray);
    }
}
