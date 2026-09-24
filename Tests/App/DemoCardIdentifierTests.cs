using System.Runtime.CompilerServices;
using LoreFetch.App.Fakes;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using LoreFetch.Core.Trigger;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// A10-prep item 4b: <see cref="DemoCardIdentifier"/> must make a captured
/// cohort show every hash-reachable <see cref="TileState"/> — this test runs
/// a real 3-card cohort through the real <see cref="ScanPipeline"/> (not a
/// mock of it) with the demo detector + demo identifier wired exactly as
/// <see cref="LoreFetch.App.AppComposition"/> wires them, and checks tile 0
/// is confident, tile 1 is low-confidence, tile 2 is unresolved.
/// </summary>
public class DemoCardIdentifierTests
{
    [Fact]
    public async Task ThreeCardCohort_ThroughRealScanPipeline_IsConfidentThenLowConfidenceThenUnresolved()
    {
        var ct = TestContext.Current.CancellationToken;

        var settings = new ScanSettings
        {
            ExpectedCount = 3,
            GoodDistance = DemoThresholds.GoodDistance,
            OkDistance = DemoThresholds.OkDistance,
        };

        var detector = new LayoutFollowingCardDetector(new StubCardDetector(3), settings);
        var rectifier = new StubRectifier();
        var identifier = new DemoCardIdentifier(new StubCardIdentifier(), settings);
        var trigger = new AutoCaptureTrigger(settings);

        using var frame = MakeFrame();
        var source = new OneFrameThenWaitSource(frame);

        var pipeline = new ScanPipeline(source, detector, rectifier, identifier, trigger, settings);

        // Subscribe BEFORE starting the loop so a fast first frame can't be
        // missed by a subscription that arrives too late.
        var frameProcessedTcs = new TaskCompletionSource();
        pipeline.FrameProcessed += (_, _) => frameProcessedTcs.TrySetResult();

        var runTask = pipeline.RunAsync(ct);
        try
        {
            await frameProcessedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            var cohort = await pipeline.CaptureAsync(ct);

            Assert.NotNull(cohort);
            Assert.Equal(3, cohort.Tiles.Count);

            // Tile 0: confident — Included, NOT low-confidence.
            Assert.Equal(TileState.Included, cohort.Tiles[0].State);
            Assert.False(cohort.Tiles[0].IsLowConfidence);

            // Tile 1: low-confidence — still Included, but flagged.
            Assert.Equal(TileState.Included, cohort.Tiles[1].State);
            Assert.True(cohort.Tiles[1].IsLowConfidence);

            // Tile 2: unresolved.
            Assert.Equal(TileState.Unresolved, cohort.Tiles[2].State);
        }
        finally
        {
            await pipeline.DisposeAsync();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (FrameSourceException)
            {
            }
        }
    }

    private static CameraFrame MakeFrame() =>
        new(
            buffer: new byte[640 * 480 * 3],
            width: 640,
            height: 480,
            stride: 640 * 3,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: null);

    /// Yields exactly one frame, then blocks (until cancelled) rather than
    /// completing the enumeration — RunAsync's loop must stay alive so the
    /// pipeline keeps holding the retained frame for CaptureAsync to consume.
    private sealed class OneFrameThenWaitSource : IFrameSource
    {
        private readonly CameraFrame _frame;

        public OneFrameThenWaitSource(CameraFrame frame) => _frame = frame;

        public string Description => "one-frame-test";

        public FrameGeometry Geometry => new(_frame.Width, _frame.Height, 0);

        public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return _frame;

            var tcs = new TaskCompletionSource();
            await using var registration = ct.Register(() => tcs.TrySetResult());
            await tcs.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
