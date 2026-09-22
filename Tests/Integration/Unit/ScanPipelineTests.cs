using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// End-to-end tests for `ScanPipeline` against the fakes — the package's own
/// acceptance suite. `CountingArrayPool` lives in CameraFrameTests.cs (same
/// namespace) and is reused here rather than duplicated.
public class ScanPipelineTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    // -- FrameProcessed --------------------------------------------------

    [Fact]
    public async Task RunAsync_RaisesFrameProcessed_OncePerFrame_WithQuadsFromTheDetector()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 4, delayMs: 2);
        var detector = new RecordingCardDetector(new StubCardDetector(cardCount: 2));
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, detector, new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        var seenQuads = new List<IReadOnlyList<CardQuad>>();
        pipeline.FrameProcessed += (_, snapshot) => seenQuads.Add(snapshot.Quads);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        var returnedByDetector = detector.Returned;
        Assert.Equal(4, returnedByDetector.Count);
        Assert.Equal(4, seenQuads.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(returnedByDetector[i], seenQuads[i]);
        }
    }

    [Fact]
    public async Task RunAsync_SnapshotCapturedAt_NeverGoesBackwards()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 12, delayMs: 2);
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        var capturedAts = new List<DateTimeOffset>();
        pipeline.FrameProcessed += (_, snapshot) => capturedAts.Add(snapshot.CapturedAt);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, capturedAts.Count);
        for (var i = 1; i < capturedAts.Count; i++)
        {
            Assert.True(
                capturedAts[i] >= capturedAts[i - 1],
                $"CapturedAt went backwards at index {i}: {capturedAts[i - 1]:o} -> {capturedAts[i]:o}");
        }
    }

    // -- Detection always asks for the maximum ---------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RunAsync_AlwaysDetectsWithMaxCards_RegardlessOfExpectedCount(int expectedCount)
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 5, delayMs: 2);
        var detector = new RecordingCardDetector(new StubCardDetector(cardCount: 1));
        var settings = new ScanSettings { ExpectedCount = expectedCount };

        await using var pipeline = new ScanPipeline(
            source, detector, new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(detector.MaxCardsCalls);
        Assert.All(detector.MaxCardsCalls, maxCards => Assert.Equal(ScanPipeline.MaxDetectionCards, maxCards));
    }

    // -- CaptureAsync: latest snapshot, null on zero quads ---------------

    [Fact]
    public async Task CaptureAsync_ZeroQuads_ReturnsNull()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2);
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 0), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.Null(cohort);
    }

    [Fact]
    public async Task CaptureAsync_NonZeroQuads_ReturnsCohortFromTheLatestSnapshot()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2);
        var settings = new ScanSettings { ExpectedCount = 3 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 3), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cohort);
        Assert.Equal(3, cohort!.Tiles.Count);
        Assert.Equal(CaptureReason.Manual, cohort.Reason);
        Assert.Equal(3, cohort.ExpectedCount);

        // Ignores ExpectedCount for what it detects/captures: a
        // detector.CardCount of 3 always yields a 3-tile cohort here, even
        // though this same assertion is exercised with ExpectedCount = 3 by
        // coincidence — the maxCards test above is what actually proves
        // ExpectedCount plays no part in detection.
    }

    // -- Dispose accounting: every pooled frame disposed exactly once ----

    [Fact]
    public async Task DisposeAsync_ReleasesTheRetainedFrame_RentsEqualReturnsAcrossManyFrames()
    {
        var pool = new CountingArrayPool();
        const int frameCount = 40;
        var source = new TestFrameSource(pool, frameCount: frameCount, delayMs: 1);
        var settings = new ScanSettings { ExpectedCount = 1 };

        var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        // RunAsync completes on its own once the finite source's
        // IAsyncEnumerable ends — no cancellation needed.
        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        // Every frame but the last was disposed by ProcessFrame's own
        // swap-and-dispose; the last one is still retained until this call.
        await pipeline.DisposeAsync();

        Assert.Equal(frameCount, pool.RentCount);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    /// Package S0.6b: a stronger variant of the test just above. That one
    /// (package S0.4a) proves rents == returns across ~40 frames with NO
    /// `CaptureAsync` calls at all, so it only ever exercises ProcessFrame's
    /// own swap-and-dispose path. `TryCaptureFromRetained` (which both
    /// `CaptureAsync` and the auto-fire path route through) disposes the
    /// frame it takes ownership of too — a SEPARATE dispose site — and
    /// nothing asserted rent == return while that site was actually firing
    /// interleaved with the loop. This test drives an order of magnitude
    /// more frames and calls `CaptureAsync` repeatedly throughout the run,
    /// so both dispose sites are live at once and the accounting still has
    /// to come out even at the end.
    ///
    /// Built via `ScanPipelineFactory.Create` — the same frozen composition
    /// entry point `Tests/Integration/EndToEnd` uses — rather than `new
    /// ScanPipeline(...)`, even though this test stays in this file
    /// (mirroring its sibling above) rather than moving to the
    /// `IImplementationSet` harness: pool accounting needs a caller-supplied
    /// `ArrayPool<byte>`, and neither `IFrameSourceFactory` nor
    /// `FolderFrameSourceFactory` (the only factory `IImplementationSet`
    /// exposes) has a seam for one — `FolderFrameSource.Open` defaults to
    /// `ArrayPool<byte>.Shared` with no override reachable through that
    /// factory. Adding one would mean editing a frozen contract.
    [Fact]
    public async Task DisposeAsync_SustainedRunWithInterleavedCaptures_RentsEqualReturnsAcrossHundredsOfFrames()
    {
        var pool = new CountingArrayPool();
        const int frameCount = 500;
        const int captureCount = 20;
        var source = new TestFrameSource(pool, frameCount: frameCount, delayMs: 1);
        var settings = new ScanSettings { ExpectedCount = 1 };

        var pipeline = ScanPipelineFactory.Create(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings, NullLoggerFactory.Instance);

        var frameProcessedCount = 0;
        pipeline.FrameProcessed += (_, _) => Interlocked.Increment(ref frameProcessedCount);

        var runTask = pipeline.RunAsync(TestContext.Current.CancellationToken);

        var successfulCaptures = 0;
        var lastObserved = 0;
        for (var i = 0; i < captureCount; i++)
        {
            // Same "strictly greater than last observed" wait as the
            // threshold test below: guarantees a frame has arrived since the
            // previous capture, rather than merely "at least one ever".
            await WaitUntilAsync(() => Volatile.Read(ref frameProcessedCount) > lastObserved, TestContext.Current.CancellationToken);
            var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);
            if (cohort is not null)
            {
                successfulCaptures++;
            }

            lastObserved = Volatile.Read(ref frameProcessedCount);
        }

        // The finite source (frameCount: 500) runs out and RunAsync
        // completes on its own — no cancellation needed, same completion
        // model as the sibling test above, but only reached after every
        // interleaved capture above has had its chance to steal a frame
        // mid-stream.
        await runTask;

        // DisposeAsync releases whichever frame is still retained when the
        // source ends: the one frame neither ProcessFrame's swap-and-dispose
        // nor any CaptureAsync call ever got to.
        await pipeline.DisposeAsync();

        Assert.True(successfulCaptures > 0, "Expected at least one CaptureAsync call to succeed mid-run.");
        Assert.Equal(frameCount, pool.RentCount);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    // -- Tile states follow ScanSettings thresholds -----------------------

    [Fact]
    public async Task CaptureAsync_TileStates_FollowScanSettingsThresholds()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: null, delayMs: 3);
        var identifier = new StubCardIdentifier();
        var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };

        var loopCts = new CancellationTokenSource();
        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), identifier,
            new ControllableTrigger(), settings);

        var frameCount = 0;
        pipeline.FrameProcessed += (_, _) => Interlocked.Increment(ref frameCount);

        var runTask = pipeline.RunAsync(loopCts.Token);
        try
        {
            // Waiting for the counter to pass its value AS OBSERVED AFTER
            // THE PREVIOUS CAPTURE — not an absolute threshold — matters
            // here: a capture can steal a frame the counter already counted
            // (production keeps running underneath it), so "count >= N" can
            // already be true with nothing fresh left retained. Waiting for
            // strictly-greater-than-last-observed guarantees a frame has
            // arrived since the last capture consumed one.
            var lastObserved = 0;

            await WaitUntilAsync(() => Volatile.Read(ref frameCount) > lastObserved, TestContext.Current.CancellationToken);
            identifier.NextDistances = new[] { GoodDistance - 10 };
            var confident = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(confident);
            Assert.Equal(TileState.Included, confident!.Tiles[0].State);
            Assert.False(confident.Tiles[0].IsLowConfidence);
            lastObserved = Volatile.Read(ref frameCount);

            await WaitUntilAsync(() => Volatile.Read(ref frameCount) > lastObserved, TestContext.Current.CancellationToken);
            identifier.NextDistances = new[] { GoodDistance + 10 };
            var lowConfidence = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(lowConfidence);
            Assert.Equal(TileState.Included, lowConfidence!.Tiles[0].State);
            Assert.True(lowConfidence.Tiles[0].IsLowConfidence);
            lastObserved = Volatile.Read(ref frameCount);

            await WaitUntilAsync(() => Volatile.Read(ref frameCount) > lastObserved, TestContext.Current.CancellationToken);
            identifier.NextDistances = new[] { OkDistance + 10 };
            var unresolved = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(unresolved);
            Assert.Equal(TileState.Unresolved, unresolved!.Tiles[0].State);
        }
        finally
        {
            await loopCts.CancelAsync();
            await AwaitLoopShutdown(runTask);
        }
    }

    // -- NotifyCaptured -----------------------------------------------

    [Fact]
    public async Task CaptureAsync_ManualCapture_CallsNotifyCapturedExactlyOnce()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2);
        var trigger = new ControllableTrigger();
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            trigger, settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);
        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cohort);
        Assert.Equal(1, trigger.NotifyCapturedCount);
    }

    [Fact]
    public async Task CaptureAsync_ZeroQuadsNoOp_DoesNotCallNotifyCaptured()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2);
        var trigger = new ControllableTrigger();
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 0), new StubRectifier(), new StubCardIdentifier(),
            trigger, settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);
        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.Null(cohort);
        Assert.Equal(0, trigger.NotifyCapturedCount);
    }

    [Fact]
    public async Task RunAsync_AutoFire_RaisesAutoCaptured_AndCallsNotifyCaptured()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2);
        var trigger = new ControllableTrigger(evaluate: (_, _, _) => true);
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            trigger, settings);

        Cohort? autoCohort = null;
        pipeline.AutoCaptured += cohort => autoCohort = cohort;

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(autoCohort);
        Assert.Equal(CaptureReason.AutoSettle, autoCohort!.Reason);
        Assert.Equal(1, trigger.NotifyCapturedCount);
    }

    // -- SourceFailed ordering ---------------------------------------------

    [Fact]
    public async Task RunAsync_RaisesSourceFailed_BeforeItsOwnTaskFaults()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: null, delayMs: 2, throwAfter: 2);
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        var sourceFailedFiredBeforeCatch = false;
        FrameSourceException? seen = null;
        pipeline.SourceFailed += ex =>
        {
            seen = ex;
            sourceFailedFiredBeforeCatch = true;
        };

        await Assert.ThrowsAsync<FrameSourceException>(
            () => pipeline.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(sourceFailedFiredBeforeCatch);
        Assert.NotNull(seen);
    }

    // -- The concurrency test that earns this package ----------------------

    [Fact]
    public async Task CaptureAsync_HammeredConcurrentlyWithTheRunningLoop_NeverThrowsAndNeverReturnsATornFrame()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: null, delayMs: 1);
        var settings = new ScanSettings { ExpectedCount = 1 };

        var loopCts = new CancellationTokenSource();
        var pipeline = new ScanPipeline(
            source, new StubCardDetector(cardCount: 1), new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        var runTask = pipeline.RunAsync(loopCts.Token);

        var exceptions = new ConcurrentBag<Exception>();
        var tornFrameCount = 0;
        var hammerCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var hammerTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!hammerCts.IsCancellationRequested)
            {
                try
                {
                    var cohort = await pipeline.CaptureAsync(CancellationToken.None);
                    if (cohort is not null)
                    {
                        foreach (var tile in cohort.Tiles)
                        {
                            var pixels = tile.Image.Pixels.Span;
                            var first = pixels[0];
                            for (var i = 1; i < pixels.Length; i++)
                            {
                                if (pixels[i] != first)
                                {
                                    Interlocked.Increment(ref tornFrameCount);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(hammerTasks);

        await loopCts.CancelAsync();
        await AwaitLoopShutdown(runTask);
        await pipeline.DisposeAsync();

        Assert.Empty(exceptions);
        Assert.Equal(0, tornFrameCount);
    }

    // -- Cohort tile order follows QuadOrdering.ReadingOrder --------------

    /// The pipeline-level proof to go with `QuadOrderingTests`' pure-function
    /// coverage: a detector handing back a 3x3 in shuffled (tie-broken)
    /// area order must still produce a `Cohort` whose TILES — not just some
    /// intermediate quad list — read out in reading order. `RectifiedCard`
    /// carries its `SourceQuad`, which is what lets this test see which
    /// physical card each tile came from without touching Abstractions.
    [Fact]
    public async Task CaptureAsync_CohortTiles_ComeOutInReadingOrder_WhenDetectorReturnsShuffledQuads()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2, width: 1200, height: 1600);
        var settings = new ScanSettings { ExpectedCount = 9 };

        var rowMajor = BuildGrid3x3(cardWidth: 300f, cardHeight: 419f, gap: 10f);
        var shuffled = Shuffle(rowMajor, seed: 12345);
        var detector = new FixedQuadDetector(shuffled);

        await using var pipeline = new ScanPipeline(
            source, detector, new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);
        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cohort);
        Assert.Equal(9, cohort!.Tiles.Count);

        // rowMajor IS reading order for this fixture (built row-major with
        // no skew), so the expectation is the fixture itself.
        for (var i = 0; i < rowMajor.Count; i++)
        {
            Assert.Equal(rowMajor[i], cohort.Tiles[i].Image.SourceQuad);
        }
    }

    /// Proves ordering is applied AFTER area-based selection, never before.
    /// The detector is handed 12 quads — 3 tiny "noise" quads placed ABOVE
    /// (smaller Y than) a real 3x3 grid, plus the 9 real cards — modelling
    /// any spec-compliant `ICardDetector` (it sorts by descending area and
    /// clamps to `maxCards` itself, exactly like `StubCardDetector`). Since
    /// the pipeline asks for `ScanPipeline.MaxDetectionCards` (9), a
    /// correct detector already drops the 3 noise quads before reading
    /// order ever sees them — survivors are chosen by AREA, not by
    /// whichever quads happen to sort first once reading order is applied.
    /// Chaos case 5 in the package brief (ask the detector for everything,
    /// apply reading order across all of it, THEN take 9) would let the
    /// noise quads — ranked first because they sit above every real card —
    /// displace real cards from the survivor set entirely; this test is
    /// what catches that.
    [Fact]
    public async Task CaptureAsync_MoreThanMaxDetectionCardsCandidates_SelectsSurvivorsByArea_NotByReadingPosition()
    {
        var pool = new CountingArrayPool();
        var source = new TestFrameSource(pool, frameCount: 1, delayMs: 2, width: 1400, height: 1800);
        var settings = new ScanSettings { ExpectedCount = 9 };

        var realCards = BuildGrid3x3(cardWidth: 300f, cardHeight: 419f, gap: 10f);
        var noise = new[]
        {
            MakeSmallQuad(centerX: 100f, centerY: 20f),
            MakeSmallQuad(centerX: 400f, centerY: 20f),
            MakeSmallQuad(centerX: 700f, centerY: 20f),
        };
        var allDetected = noise.Concat(realCards).ToList(); // 12 total, > MaxDetectionCards (9)

        var detector = new AreaClampingQuadDetector(allDetected);

        await using var pipeline = new ScanPipeline(
            source, detector, new StubRectifier(), new StubCardIdentifier(),
            new ControllableTrigger(), settings);

        await pipeline.RunAsync(TestContext.Current.CancellationToken);
        var cohort = await pipeline.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cohort);
        Assert.Equal(9, cohort!.Tiles.Count);

        var survivorAreas = cohort.Tiles.Select(t => t.Image.SourceQuad.AreaPx).ToList();
        Assert.All(survivorAreas, area => Assert.True(
            area > 10_000f,
            $"Every surviving tile must be a real card (area ~125,700), never a noise quad (area 400); got {area}."));
    }

    private static CardQuad MakeSmallQuad(float centerX, float centerY)
    {
        const float half = 10f; // 20x20 "noise" quad -- far smaller than any real card
        return new CardQuad(
            TL: new PointF2(centerX - half, centerY - half),
            TR: new PointF2(centerX + half, centerY - half),
            BR: new PointF2(centerX + half, centerY + half),
            BL: new PointF2(centerX - half, centerY + half));
    }

    /// Models a spec-compliant `ICardDetector`: sorts everything it was
    /// given by descending area and clamps to `maxCards` ITSELF, exactly
    /// as `ICardDetector.Detect`'s own contract requires. Selection is
    /// therefore already done before this fake ever returns — the pipeline
    /// (and `QuadOrdering` after it) never sees the discarded candidates.
    private sealed class AreaClampingQuadDetector : ICardDetector
    {
        private readonly IReadOnlyList<CardQuad> _all;

        public AreaClampingQuadDetector(IReadOnlyList<CardQuad> all) => _all = all;

        public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards) =>
            _all.OrderByDescending(q => q.AreaPx).Take(maxCards).ToList();
    }

    private static List<CardQuad> BuildGrid3x3(float cardWidth, float cardHeight, float gap)
    {
        const float originX = 60f;
        const float originY = 60f;

        var quads = new List<CardQuad>(9);
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var centerX = originX + (cardWidth / 2f) + (c * (cardWidth + gap));
                var centerY = originY + (cardHeight / 2f) + (r * (cardHeight + gap));
                var hw = cardWidth / 2f;
                var hh = cardHeight / 2f;
                quads.Add(new CardQuad(
                    TL: new PointF2(centerX - hw, centerY - hh),
                    TR: new PointF2(centerX + hw, centerY - hh),
                    BR: new PointF2(centerX + hw, centerY + hh),
                    BL: new PointF2(centerX - hw, centerY + hh)));
            }
        }

        return quads;
    }

    private static List<T> Shuffle<T>(IReadOnlyList<T> items, int seed)
    {
        var list = items.ToList();
        var rng = new Random(seed); // fixed seed: deterministic test
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// An `ICardDetector` that always returns a fixed quad list, clamped to
    /// `maxCards` — models a real detector handing the pipeline quads in
    /// whatever (tie-broken) area order it produced, without needing a real
    /// contour pass.
    private sealed class FixedQuadDetector : ICardDetector
    {
        private readonly IReadOnlyList<CardQuad> _quads;

        public FixedQuadDetector(IReadOnlyList<CardQuad> quads) => _quads = quads;

        public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards) =>
            _quads.Take(maxCards).ToList();
    }

    // -- Test doubles --------------------------------------------------

    /// A frame source with total control over count, pacing and failure, so
    /// pipeline tests can be deterministic rather than timing-dependent.
    /// Every frame's pixels are filled with ONE repeated byte, distinct per
    /// frame index — `StubRectifier` only copies, never blends, so a
    /// rectified card's pixels are either entirely one such value (a clean
    /// read) or a mix of two (a torn read off a frame reused mid-copy).
    private sealed class TestFrameSource : IFrameSource
    {
        private readonly ArrayPool<byte> _pool;
        private readonly int? _frameCount;
        private readonly int _delayMs;
        private readonly int? _throwAfter;
        private readonly int _width;
        private readonly int _height;

        public TestFrameSource(
            ArrayPool<byte> pool, int? frameCount, int delayMs, int? throwAfter = null,
            int width = 64, int height = 64)
        {
            _pool = pool;
            _frameCount = frameCount;
            _delayMs = delayMs;
            _throwAfter = throwAfter;
            _width = width;
            _height = height;
            Geometry = new FrameGeometry(width, height, RotationDegrees: 0);
        }

        public string Description => "test-frame-source";

        public FrameGeometry Geometry { get; }

        public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var index = 0;
            while (_frameCount is not int max || index < max)
            {
                // Delay BEFORE renting, so a cancellation here never leaves
                // a rented-but-never-yielded buffer for the test to miss in
                // its rent/return accounting.
                await Task.Delay(_delayMs, ct).ConfigureAwait(false);

                if (_throwAfter is int throwAt && index >= throwAt)
                {
                    throw new FrameSourceException("Test frame source: simulated device failure.");
                }

                yield return MakeFrame(index);
                index++;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private CameraFrame MakeFrame(int index)
        {
            var stride = _width * 3;
            var length = stride * _height;
            var buffer = _pool.Rent(length);

            var fillValue = (byte)(1 + (index % 250));
            Array.Fill(buffer, fillValue, 0, length);

            return new CameraFrame(buffer, _width, _height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, _pool);
        }
    }

    /// Wraps a real `ICardDetector` and records every call's `maxCards`
    /// argument and returned quads, so a test can assert on both without
    /// the fake itself needing to know it is being watched.
    private sealed class RecordingCardDetector : ICardDetector
    {
        private readonly ICardDetector _inner;
        private readonly object _lock = new();
        private readonly List<int> _maxCardsCalls = new();
        private readonly List<IReadOnlyList<CardQuad>> _returned = new();

        public RecordingCardDetector(ICardDetector inner) => _inner = inner;

        public IReadOnlyList<int> MaxCardsCalls
        {
            get { lock (_lock) { return _maxCardsCalls.ToList(); } }
        }

        public IReadOnlyList<IReadOnlyList<CardQuad>> Returned
        {
            get { lock (_lock) { return _returned.ToList(); } }
        }

        public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards)
        {
            var result = _inner.Detect(frame, maxCards);
            lock (_lock)
            {
                _maxCardsCalls.Add(maxCards);
                _returned.Add(result);
            }

            return result;
        }
    }

    /// An `IAutoCaptureTrigger` whose firing condition and call counts are
    /// fully controlled by the test, rather than needing the real
    /// (stream-A-owned) settle-timer implementation.
    private sealed class ControllableTrigger : IAutoCaptureTrigger
    {
        private readonly Func<IReadOnlyList<CardQuad>, int, DateTimeOffset, bool> _evaluate;

        public ControllableTrigger(Func<IReadOnlyList<CardQuad>, int, DateTimeOffset, bool>? evaluate = null) =>
            _evaluate = evaluate ?? ((_, _, _) => false);

        public int NotifyCapturedCount { get; private set; }

        public int ResetCount { get; private set; }

        public bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now) =>
            _evaluate(quads, expectedCount, now);

        public void NotifyCaptured() => NotifyCapturedCount++;

        public void Reset() => ResetCount++;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    /// Awaits a loop task that was stopped via cancellation, treating
    /// OperationCanceledException as the expected shutdown outcome rather
    /// than a test failure.
    private static async Task AwaitLoopShutdown(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop observed the cancellation it was asked for.
        }
    }
}
