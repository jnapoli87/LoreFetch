using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.Accuracy;
using LoreFetch.Lab.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Lab.Accuracy;

/// **This test is the harness's own self-test, not an accuracy result.**
/// Every card here is a procedural "card-like" image (`SyntheticImages`,
/// orchestration finding V13), never a real Scryfall render or camera
/// photo, and the tiny in-test index below is not the committed
/// `cards.lfidx` -- nothing in this class may be quoted as a LoreFetch
/// accuracy figure. Its job is to drive the FULL real pipeline
/// (`ContourCardDetector` -&gt; `PerspectiveRectifier` -&gt;
/// `HashCardIdentifier`) through `AccuracyFrameRunner`, so the harness
/// itself is proven correct before the real H3 corpus exists (this
/// package's brief: "You are NOT producing the accuracy numbers... your
/// job is to have a correct, debugged harness ready").
///
/// `MultiCardFrameGenerator` (package B6, not B7) exists specifically so
/// this test can composite MULTIPLE cards into one frame -- B7's own
/// `SyntheticFrameGenerator` only ever places one card at the frame
/// center, which cannot exercise row-major slot mapping or a count
/// mismatch at all.
public class AccuracyHarnessSyntheticTests
{
    private readonly ITestOutputHelper _output;

    public AccuracyHarnessSyntheticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// A 3-in-a-row frame where the SOURCE order handed to
    /// `MultiCardFrameGenerator` deliberately does NOT match physical
    /// left-to-right order (card "B" is generated first but placed on the
    /// physical LEFT, "A" second but placed in the MIDDLE... see the
    /// comment on `centers` below) -- so a correct result here depends on
    /// `SlotMapper`'s row-major CENTROID sort, not on detection or
    /// generation order, end to end through the REAL detector.
    [Fact]
    public void Run_ThreeCardRow_PhysicalLeftToRightOrderWinsRegardlessOfGenerationOrder()
    {
        var cardA = MakeProceduralCard(seed: 1, "art-a", "oracle-a", "Card A");
        var cardB = MakeProceduralCard(seed: 2, "art-b", "oracle-b", "Card B");
        var cardC = MakeProceduralCard(seed: 3, "art-c", "oracle-c", "Card C");
        using (cardA.Bgr)
        using (cardB.Bgr)
        using (cardC.Bgr)
        {
            var index = BuildIndex(cardA, cardB, cardC);
            var identifier = new HashCardIdentifier(index);

            // Generator order [A, B, C] but PHYSICAL centers place B on the
            // left (x=0.15), C in the middle (x=0.5), A on the right
            // (x=0.85) -- so physical row-major slot order is B, C, A,
            // which does not match either the generator's input order or
            // (necessarily) whatever order the real detector's contour
            // scan happens to return them in.
            var sources = new[] { cardA.Bgr, cardB.Bgr, cardC.Bgr };
            var centers = new[] { (0.85f, 0.5f), (0.15f, 0.5f), (0.5f, 0.5f) }; // A, B, C in that order

            using var frame = MultiCardFrameGenerator.Generate(sources, centers, heightInches: 15f);

            var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(frame, maxCards: 3);
            Assert.Equal(3, detected.Count);

            var groundTruth = BuildGroundTruthFrame(layout: 3, ("f.png", 15),
                cardB.OracleName, cardC.OracleName, cardA.OracleName); // physical left-to-right = B, C, A

            var resolved = GroundTruthOracleLookup.Resolve(index, groundTruth.Select(r => r).ToList());
            var groundTruthFrame = GroundTruthFrame.GroupByFile(resolved).Single();

            var rectifier = new PerspectiveRectifier();
            var results = AccuracyFrameRunner.Run(groundTruthFrame, frame, detector, rectifier, identifier, AccuracyHarnessOptions.Default);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(SlotOutcome.Correct, r.Outcome));
            Assert.Equal("oracle-b", results[0].Rank1OracleId); // slot 1 = leftmost = B
            Assert.Equal("oracle-c", results[1].Rank1OracleId); // slot 2 = middle = C
            Assert.Equal("oracle-a", results[2].Rank1OracleId); // slot 3 = rightmost = A
        }
    }

    /// The full B6 report on a 9-card frame, including exactly one basic
    /// land -- proving `AccuracyStatistics` excludes it from the headline
    /// (with the count printed) while still classifying it (as Correct,
    /// here) in the full breakdown, end to end through the real pipeline.
    [Fact]
    public void Run_NineCardGrid_LandExcludedFromHeadlineButClassifiedInBreakdown()
    {
        var cards = Enumerable.Range(0, 9)
            .Select(i => MakeProceduralCard(seed: 10 + i, $"art-{i}", $"oracle-{i}", $"Card {i}"))
            .ToList();
        // Card 4 (the grid's center slot) is the one basic land.
        cards[4] = cards[4] with { IsBasicLand = true, OracleName = "Forest" };

        try
        {
            var index = BuildIndex(cards.ToArray());
            var identifier = new HashCardIdentifier(index);

            var (sources, centers) = GridOf(cards);
            var options = new MultiCardFrameOptions { FrameWidth = 1920, FrameHeight = 1400 };
            using var frame = MultiCardFrameGenerator.Generate(sources, centers, heightInches: 15f, options);

            var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(frame, maxCards: 9);
            Assert.Equal(9, detected.Count);

            var rows = BuildGroundTruthFrame(layout: 9, ("f.png", 15), cards.Select(c => c.OracleName).ToArray());
            var resolved = GroundTruthOracleLookup.Resolve(index, rows);
            var groundTruthFrame = GroundTruthFrame.GroupByFile(resolved).Single();

            var rectifier = new PerspectiveRectifier();
            var results = AccuracyFrameRunner.Run(groundTruthFrame, frame, detector, rectifier, identifier, AccuracyHarnessOptions.Default);

            Assert.All(results, r => Assert.Equal(SlotOutcome.Correct, r.Outcome));

            var stats = AccuracyStatistics.From(results);
            Assert.Equal(1, stats.ExcludedLandCount);
            Assert.Equal(8, stats.Headline.Total); // 9 slots - 1 land = 8 headline slots
            Assert.Equal(8, stats.Headline.Correct);

            var fullBreakdownTotal = stats.Breakdown.Sum(row => row.Buckets.Total);
            Assert.Equal(9, fullBreakdownTotal); // the land IS present in the full breakdown

            // Print the full report exactly as `lab accuracy`/the real-capture
            // test would -- this is what B6's brief asks to be quoted as the
            // harness's OWN self-test output, clearly not an accuracy result:
            // every card is procedural, and the index is a 9-entry in-memory
            // one, not the committed cards.lfidx.
            var coverage = new CorpusCoverage(
                FramesInGroundTruth: 1, FramesFoundOnDisk: 1,
                HeightsCovered: [15.0], RungsCovered: ["normal"], MatsCovered: ["light"]);
            var gate = AccuracyGateResult.Evaluate(stats, AccuracyHarnessOptions.Default);
            _output.WriteLine("=== HARNESS SELF-TEST (synthetic, in-memory index) -- NOT a LoreFetch accuracy result ===");
            _output.WriteLine(AccuracyReportFormatter.Format(coverage, stats, gate, AccuracyHarnessOptions.Default));
        }
        finally
        {
            foreach (var card in cards)
            {
                card.Bgr.Dispose();
            }
        }
    }

    /// Task 2 (docs/history/orchestration-plan.md) end to end through the REAL detector:
    /// a 3x3 frame with a card PHYSICALLY missing from one cell (not just a
    /// ground-truth mismatch) -- the closest synthetic reproduction of the
    /// real corpus's own dominant failure mode (most `a_corpus` frames find
    /// 8 of 9 -- see docs/accuracy.md). Grid inference must classify the 8
    /// present cards normally and mark the empty cell `NoDetection`, not
    /// drop the whole frame the way pre-Task-2 `TryMapToSlots` would have.
    [Fact]
    public void Run_EightOfNinePhysicallyPresent_ClassifiesEightAndMarksTheEmptyCellNoDetection()
    {
        var cards = Enumerable.Range(0, 9)
            .Select(i => MakeProceduralCard(seed: 20 + i, $"art-e2e-{i}", $"oracle-e2e-{i}", $"Grid Card {i}"))
            .ToList();

        try
        {
            var index = BuildIndex(cards.ToArray());
            var identifier = new HashCardIdentifier(index);

            var (allSources, allCenters) = GridOf(cards);
            // Physically omit the CENTER card (index 4) from the composited
            // frame -- everything else about the layout (positions,
            // options) is identical to the full 9-card test above.
            var sources = allSources.Where((_, i) => i != 4).ToArray();
            var centers = allCenters.Where((_, i) => i != 4).ToArray();

            var options = new MultiCardFrameOptions { FrameWidth = 1920, FrameHeight = 1400 };
            using var frame = MultiCardFrameGenerator.Generate(sources, centers, heightInches: 15f, options);

            var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(frame, maxCards: 9);
            Assert.Equal(8, detected.Count); // only 8 physically exist

            var rows = BuildGroundTruthFrame(layout: 9, ("f.png", 15), cards.Select(c => c.OracleName).ToArray());
            var resolved = GroundTruthOracleLookup.Resolve(index, rows);
            var groundTruthFrame = GroundTruthFrame.GroupByFile(resolved).Single();

            var rectifier = new PerspectiveRectifier();
            var results = AccuracyFrameRunner.Run(groundTruthFrame, frame, detector, rectifier, identifier, AccuracyHarnessOptions.Default);

            Assert.Equal(9, results.Count); // one per ground-truth slot -- the missing one is NOT dropped from the list
            Assert.Equal(SlotOutcome.NoDetection, results[4].Outcome); // slot 5 = grid center = the omitted card
            Assert.Null(results[4].Rank1OracleId);

            for (var i = 0; i < 9; i++)
            {
                if (i == 4)
                {
                    continue;
                }

                Assert.Equal(SlotOutcome.Correct, results[i].Outcome);
            }

            var stats = AccuracyStatistics.From(results);
            Assert.Equal(9, stats.Headline.Total); // every slot accounted for -- the required 100% sum
            Assert.Equal(8, stats.Headline.Correct);
            Assert.Equal(1, stats.Headline.NoDetection);
            Assert.Equal(0, stats.Headline.DroppedFrame); // NOT dropped -- this is Task 2's whole point
        }
        finally
        {
            foreach (var card in cards)
            {
                card.Bgr.Dispose();
            }
        }
    }

    /// **The single highest-value test in the package** (H3 note block),
    /// run end to end through the REAL detector: a frame that physically
    /// contains 3 cards, but whose ground-truth layout claims 4 slots (the
    /// 4th naming a card that is not actually present -- standing in for
    /// B5a's own observed failure, a card that went undetected). Every
    /// slot must come back `DroppedFrame`, and -- the actual assertion
    /// that matters -- NONE of the three real cards' correct identifications
    /// may be misassigned to the wrong ground-truth slot as a side effect
    /// of the count mismatch. If `AccuracyFrameRunner` ever zipped quads to
    /// slots by index instead of refusing outright, this would instead
    /// show 3 real (but MISALIGNED) classifications plus one spurious
    /// unresolved slot -- exactly the failure this test exists to catch.
    [Fact]
    public void Run_LayoutClaimsOneMoreCardThanIsPhysicallyPresent_DropsEveryHouseSlot_NoMisalignment()
    {
        var cardA = MakeProceduralCard(seed: 1, "art-a", "oracle-a", "Card A");
        var cardB = MakeProceduralCard(seed: 2, "art-b", "oracle-b", "Card B");
        var cardC = MakeProceduralCard(seed: 3, "art-c", "oracle-c", "Card C");
        var phantom = MakeProceduralCard(seed: 4, "art-phantom", "oracle-phantom", "Phantom Card"); // never composited
        using (cardA.Bgr)
        using (cardB.Bgr)
        using (cardC.Bgr)
        using (phantom.Bgr)
        {
            var index = BuildIndex(cardA, cardB, cardC, phantom); // phantom IS in the index -- it's just not in the frame
            var identifier = new HashCardIdentifier(index);

            var sources = new[] { cardA.Bgr, cardB.Bgr, cardC.Bgr };
            var centers = new[] { (0.15f, 0.5f), (0.5f, 0.5f), (0.85f, 0.5f) };
            using var frame = MultiCardFrameGenerator.Generate(sources, centers, heightInches: 15f);

            var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
            var detected = detector.Detect(frame, maxCards: 4); // asked for 4 (the ground truth's own layout)
            Assert.Equal(3, detected.Count); // only 3 physically exist -- Detect cannot invent the 4th

            var rows = BuildGroundTruthFrame(layout: 4, ("f.png", 15),
                cardA.OracleName, cardB.OracleName, cardC.OracleName, phantom.OracleName);
            var resolved = GroundTruthOracleLookup.Resolve(index, rows);
            var groundTruthFrame = GroundTruthFrame.GroupByFile(resolved).Single();

            var rectifier = new PerspectiveRectifier();
            var results = AccuracyFrameRunner.Run(groundTruthFrame, frame, detector, rectifier, identifier, AccuracyHarnessOptions.Default);

            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.Equal(SlotOutcome.DroppedFrame, r.Outcome));
            Assert.All(results, r => Assert.Equal(3, r.DetectedCountInFrame));

            // The real proof this is not silent misalignment: none of the
            // three genuinely-present, genuinely-identifiable cards leaked
            // through as a Correct/Wrong classification under ANY slot.
            Assert.All(results, r => Assert.Null(r.Rank1OracleId));
            Assert.DoesNotContain(results, r => r.Outcome == SlotOutcome.Correct);
            Assert.DoesNotContain(results, r => r.Outcome == SlotOutcome.Wrong);
        }
    }

    private static (Mat[] Sources, (float XFrac, float YFrac)[] Centers) GridOf(IReadOnlyList<ProceduralCard> cards)
    {
        Assert.Equal(9, cards.Count);
        var xFracs = new[] { 0.2f, 0.5f, 0.8f };
        var yFracs = new[] { 0.15f, 0.5f, 0.85f };

        var sources = cards.Select(c => c.Bgr).ToArray();
        var centers = new (float, float)[9];
        var i = 0;
        foreach (var y in yFracs)
        {
            foreach (var x in xFracs)
            {
                centers[i++] = (x, y);
            }
        }

        return (sources, centers);
    }

    private static IReadOnlyList<GroundTruthRow> BuildGroundTruthFrame(
        int layout, (string File, double HeightIn) frame, params string[] oracleNamesInSlotOrder)
    {
        Assert.Equal(layout, oracleNamesInSlotOrder.Length);
        var rows = new List<GroundTruthRow>(layout);
        for (var i = 0; i < oracleNamesInSlotOrder.Length; i++)
        {
            rows.Add(new GroundTruthRow(frame.File, frame.HeightIn, layout, i + 1, oracleNamesInSlotOrder[i], "normal", "light"));
        }

        return rows;
    }

    private static HashIndexData BuildIndex(params ProceduralCard[] cards)
    {
        var entries = new List<HashIndexEntry>(cards.Length);
        var oracleTable = new List<OracleEntry>(cards.Length);
        foreach (var card in cards)
        {
            using var gray = ReferenceTransform.Prepare(card.Bgr);
            var hash = CardHasher.Hash(gray);
            entries.Add(new HashIndexEntry(hash, card.OracleId, card.OracleName, card.ArtworkId, card.IsBasicLand));
            oracleTable.Add(new OracleEntry(card.OracleId, card.OracleName));
        }

        return new HashIndexData(entries, oracleTable);
    }

    /// Same recipe `SyntheticFrameGeneratorRoundTripTests` uses (darken for
    /// card-like border contrast, then stamp a seed-random circle as
    /// distinctive "art" so different seeds hash apart) -- re-derived here
    /// rather than shared, since that recipe lives as private methods on
    /// that other test class.
    private static ProceduralCard MakeProceduralCard(int seed, string artworkId, string oracleId, string oracleName)
    {
        using var raw = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed);
        var darker = new Mat();
        raw.ConvertTo(darker, MatType.CV_8UC3, alpha: 0.4, beta: 0);

        var random = new Random(seed);
        var cx = darker.Cols * (0.25 + (0.5 * random.NextDouble()));
        var cy = darker.Rows * 0.85 * (0.25 + (0.5 * random.NextDouble()));
        var radius = darker.Cols * (0.14 + (0.10 * random.NextDouble()));
        var color = new Scalar(random.Next(40, 220), random.Next(40, 220), random.Next(40, 220));
        Cv2.Circle(darker, new Point((int)cx, (int)cy), (int)radius, color, thickness: -1);

        return new ProceduralCard(darker, artworkId, oracleId, oracleName, IsBasicLand: false);
    }

    private sealed record ProceduralCard(Mat Bgr, string ArtworkId, string OracleId, string OracleName, bool IsBasicLand);
}
