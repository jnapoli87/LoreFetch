using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.Synthetic;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Package B7's own acceptance test: "a generated card at 9.75in retrieves
/// its own artwork" -- run entirely through the SAME three calls the
/// shipping scan pipeline makes (`ICardDetector.Detect` ->
/// `IRectifier.Rectify` -> `ICardIdentifier.Identify`), against an index
/// built INSIDE this test from procedural card-like images (orchestration
/// finding V13 -- CI has no real Scryfall renders to synthesize from, so
/// this never touches the committed `data/index/cards.lfidx` and never
/// needs a real card image on disk).
///
/// Why this is the STRUCTURAL guard the package brief calls for, not just
/// a comment: `SyntheticFrameGenerator.Generate` returns a `CameraFrame` --
/// a whole scene, mat background and all -- and nothing else. There is no
/// `SyntheticFrameGenerator` overload that hands back a `RectifiedCard` or
/// a hash. The only way this test (or any other caller) can find out what
/// card a generated frame shows is to run it through `Detect` ->
/// `Rectify` -> `Identify`, exactly as written below. A test that instead
/// tried to hash the generator's OWN output directly (skipping detection
/// and rectification entirely) would be testing a different, unshipped
/// code path -- see `RoundTrip_BypassingTheShippingPath_DoesNotMatch`,
/// this package's chaos case (a), for what that shortcut actually produces.
public class SyntheticFrameGeneratorRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public SyntheticFrameGeneratorRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// Builds `cardCount` distinct procedural "card-like" renders (the same
    /// `SyntheticImages.MakeCardLikeBgr` B1a's own hash tests already rely
    /// on -- deterministic, seeded, never a file on disk), hashes each one
    /// exactly the way `LoreFetch.Lab build-index` hashes a real Scryfall
    /// render (`ReferenceTransform.Prepare` -> `CardHasher.Hash`), and
    /// wraps the result as an in-memory `HashIndexData` -- no
    /// `HashIndexFile.Write`/`Read` round trip needed, since
    /// `HashCardIdentifier`'s own constructor already accepts a
    /// `HashIndexData` directly for exactly this reason (see its own doc
    /// comment: "tests -- and B7's index-builder tests -- can hand in a
    /// HashIndexData built in memory, with no file on disk at all").
    [Fact]
    public void RoundTrip_GeneratedCardAtNineSeventyFiveInches_RetrievesItsOwnArtwork()
    {
        const int cardCount = 24;
        const int querySeed = 5;

        var cards = BuildProceduralCards(cardCount);
        var index = BuildIndex(cards);
        var identifier = new HashCardIdentifier(index);

        var queryCard = cards[querySeed];
        var result = SyntheticFrameGenerator.Generate(queryCard.Bgr, heightInches: 9.75f);

        using (result.Frame)
        {
            var detector = new ContourCardDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(result.Frame, maxCards: 1);
            Assert.Single(detected);

            var rectifier = new PerspectiveRectifier();
            var rectified = rectifier.Rectify(result.Frame, detected[0]);

            var candidates = identifier.Identify(rectified, maxCandidates: 5);
            Assert.NotEmpty(candidates);

            var top = candidates[0];
            _output.WriteLine(
                $"RoundTrip: expected ArtworkId={queryCard.ArtworkId}, got rank-1 ArtworkId={top.ArtworkId}, " +
                $"Distance={top.Distance} (rank-2 distance: {(candidates.Count > 1 ? candidates[1].Distance.ToString() : "n/a")})");

            Assert.Equal(queryCard.ArtworkId, top.ArtworkId);
        }
    }

    /// The project's recorded operating height moved to 12in after this
    /// package's brief was written (9.75in leaves a 3x3 grid 0.04in of
    /// margin -- a geometric floor, not a usable height; 12in leaves
    /// 1.83in). Camera orientation is also now landscape, unrotated. Kept
    /// alongside -- not in place of -- the 9.75in test above, which stays
    /// the package's own stated acceptance criterion and untouched.
    [Fact]
    public void RoundTrip_GeneratedCardAtTwelveInches_RetrievesItsOwnArtwork()
    {
        const int cardCount = 24;
        const int querySeed = 5;

        var cards = BuildProceduralCards(cardCount);
        var index = BuildIndex(cards);
        var identifier = new HashCardIdentifier(index);

        var queryCard = cards[querySeed];
        var result = SyntheticFrameGenerator.Generate(queryCard.Bgr, heightInches: 12f);

        using (result.Frame)
        {
            var detector = new ContourCardDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(result.Frame, maxCards: 1);
            Assert.Single(detected);

            var rectifier = new PerspectiveRectifier();
            var rectified = rectifier.Rectify(result.Frame, detected[0]);

            var candidates = identifier.Identify(rectified, maxCandidates: 5);
            Assert.NotEmpty(candidates);

            var top = candidates[0];
            _output.WriteLine(
                $"RoundTrip@12in: expected ArtworkId={queryCard.ArtworkId}, got rank-1 ArtworkId={top.ArtworkId}, " +
                $"Distance={top.Distance} (rank-2 distance: {(candidates.Count > 1 ? candidates[1].Distance.ToString() : "n/a")})");

            Assert.Equal(queryCard.ArtworkId, top.ArtworkId);
        }
    }

    /// Chaos case (a) (brief-mandated): "hash the generated image directly
    /// instead of going through detect -> rectify". Taken literally: wrap
    /// the generator's own OUTPUT `CameraFrame` -- the whole 1920x1080
    /// scene, mat background and all, exactly as `Generate` returns it,
    /// with NO detection and NO rectification applied -- straight into a
    /// `RectifiedCard` (same `Pixels`/`Stride`/`Layout`, just reinterpreted
    /// as if it were already a canonical 488x680 card) and identify that.
    /// This is the shortcut a caller could ONLY take if
    /// `SyntheticFrameGenerator` exposed its raw pixels as something other
    /// than an opaque `CameraFrame` -- which is exactly why the acceptance
    /// test above has no such shortcut available to it.
    ///
    /// `QueryTransform.ToMat` reads exactly `RectifiedCard.CanonicalWidth` x
    /// `CanonicalHeight` bytes per the card's OWN declared `Stride`, so
    /// reinterpreting a wider frame this way is well-defined (it degenerates
    /// to reading the top-left 488x680 corner of the 1920x1080 frame at the
    /// frame's own row stride) -- since `SyntheticFrameGenerator` composites
    /// the card at the FRAME'S CENTER, not its top-left corner, that corner
    /// is pure mat background, so this is expected to identify nothing like
    /// the query card at all, confirming the bypass is not a no-op.
    ///
    /// This is the chaos double-check for the structural guard above, not a
    /// regression test for a bug that was ever shipped -- there is no
    /// "revert the fix" step here because there is no fix; the point is
    /// that skipping detect/rectify is DETECTABLY wrong, which confirms the
    /// acceptance test above is not vacuously passing regardless of path.
    [Fact]
    public void RoundTrip_BypassingTheShippingPath_DoesNotMatch()
    {
        const int cardCount = 24;
        const int querySeed = 5;

        var cards = BuildProceduralCards(cardCount);
        var index = BuildIndex(cards);
        var identifier = new HashCardIdentifier(index);

        var queryCard = cards[querySeed];
        var result = SyntheticFrameGenerator.Generate(queryCard.Bgr, heightInches: 9.75f);

        using (result.Frame)
        {
            var bypassedCard = new RectifiedCard(
                result.Frame.Pixels.ToArray(), result.Frame.Stride, result.Frame.Layout, DummyQuad());

            var candidates = identifier.Identify(bypassedCard, maxCandidates: 5);
            Assert.NotEmpty(candidates);

            var top = candidates[0];
            _output.WriteLine(
                $"Bypass: expected ArtworkId={queryCard.ArtworkId}, got rank-1 ArtworkId={top.ArtworkId}, Distance={top.Distance}.");

            Assert.NotEqual(queryCard.ArtworkId, top.ArtworkId);
        }
    }

    private static CardQuad DummyQuad() => new(
        TL: new PointF2(0, 0), TR: new PointF2(1, 0), BR: new PointF2(1, 1), BL: new PointF2(0, 1));

    /// `SyntheticImages.MakeCardLikeBgr` spans the FULL 0-255 range corner
    /// to corner by design (B1a's own invariant tests want that spread).
    /// Composited as-is against `SyntheticFrameGenerator`'s default 170
    /// mat brightness, its diagonal necessarily crosses 170 somewhere along
    /// every edge of the card -- and right at that crossing, local
    /// card-vs-mat contrast (what Canny's gradient magnitude actually
    /// measures) drops toward zero, breaking the outer edge into
    /// disconnected arcs instead of one closed contour. Real cards do not
    /// have this problem: they carry a black border (CLAUDE.md "Card
    /// detection") that keeps their OUTER edge dark and high-contrast
    /// against any mat, light or dark. Darkening the procedural fixture to
    /// 0-45% of its original range reproduces that property -- a uniformly
    /// darker image, not a lower-contrast one: `ConvertTo`'s `alpha` scales
    /// every channel by the same factor, so each cell's OWN internal
    /// relative brightness (what `CardHasher`'s per-cell median test
    /// actually reacts to) and the per-seed noise are both preserved
    /// exactly, just at a lower absolute level that stays below the mat
    /// brightness end to end.
    private static Mat DarkenForCardLikeContrast(Mat bgr)
    {
        var darker = new Mat();
        bgr.ConvertTo(darker, MatType.CV_8UC3, alpha: 0.4, beta: 0);
        return darker;
    }

    /// `SyntheticImages.MakeCardLikeBgr` differs between seeds ONLY in its
    /// independent per-pixel noise (+-15) laid over the SAME deterministic
    /// diagonal gradient -- plenty for B1a's own bound-based invariant
    /// tests, which compare a hash against ITSELF under a transform, but
    /// not enough low-frequency structure to keep two DIFFERENT seeds
    /// reliably apart once `ReferenceTransform`'s 96px resize and
    /// `CardHasher`'s 32x32 resize have both averaged most of that
    /// pixel-level noise away: an early version of this test measured 24
    /// such "cards" landing at rank-1/rank-2 distances of 55 and 56 --
    /// a 1-bit margin, indistinguishable from noise, and NOT a finding
    /// about the shipping hash (see the round-trip test's own distance
    /// log for what a real card-shaped source produces once this is
    /// fixed). This helper adds a seed-random filled circle -- a stand-in
    /// "art" feature with genuinely different position, size and color per
    /// seed -- inside the region `CardHasher` actually crops (the top
    /// ~61%), so each synthetic "artwork" carries real low-frequency
    /// structure that survives both downsamples, the way a real card's
    /// varied illustration does.
    private static Mat AddDistinctiveArt(Mat bgr, int seed)
    {
        var random = new Random(seed);
        var width = bgr.Cols;
        var height = bgr.Rows;

        var cx = width * (0.25 + (0.5 * random.NextDouble()));
        var cy = height * 0.85 * (0.25 + (0.5 * random.NextDouble())); // within CardHasher's own top-0.85w-tall region
        var radius = width * (0.14 + (0.10 * random.NextDouble()));
        var color = new Scalar(random.Next(40, 220), random.Next(40, 220), random.Next(40, 220));

        Cv2.Circle(bgr, new Point((int)cx, (int)cy), (int)radius, color, thickness: -1);
        return bgr;
    }

    private static IReadOnlyList<ProceduralCard> BuildProceduralCards(int count)
    {
        var cards = new List<ProceduralCard>(count);
        for (var i = 0; i < count; i++)
        {
            using var raw = SyntheticImages.MakeCardLikeBgr(
                RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 1000 + i);
            var bgr = AddDistinctiveArt(DarkenForCardLikeContrast(raw), seed: 1000 + i);
            cards.Add(new ProceduralCard(bgr, ArtworkId: $"synthetic-art-{i:D4}", OracleId: $"synthetic-oracle-{i:D4}", OracleName: $"Synthetic Card {i}"));
        }

        return cards;
    }

    private static HashIndexData BuildIndex(IReadOnlyList<ProceduralCard> cards)
    {
        var entries = new List<HashIndexEntry>(cards.Count);
        var oracleTable = new List<OracleEntry>(cards.Count);

        foreach (var card in cards)
        {
            using var gray = ReferenceTransform.Prepare(card.Bgr);
            var hash = CardHasher.Hash(gray);
            entries.Add(new HashIndexEntry(hash, card.OracleId, card.OracleName, card.ArtworkId, IsBasicLand: false));
            oracleTable.Add(new OracleEntry(card.OracleId, card.OracleName));
        }

        return new HashIndexData(entries, oracleTable);
    }

    private sealed record ProceduralCard(Mat Bgr, string ArtworkId, string OracleId, string OracleName);
}
