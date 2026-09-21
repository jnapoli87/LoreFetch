using System.Buffers;
using System.Runtime.CompilerServices;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// `ScanPipelineFactory.Create` — package S0.4b. Proves the factory wires
/// the same thing the hand-wired `ScanPipeline` tests in
/// `ScanPipelineTests.cs` wire directly: a pipeline built through
/// `Create(...)`, run against the real fakes, still raises `FrameProcessed`.
public class ScanPipelineFactoryTests
{
    [Fact]
    public async Task Create_BuildsAWorkingPipeline_ThatRaisesFrameProcessed()
    {
        var pool = ArrayPool<byte>.Shared;
        var source = new OneShotFrameSource(pool, frameCount: 3);
        var settings = new ScanSettings { ExpectedCount = 1 };

        await using var pipeline = ScanPipelineFactory.Create(
            source,
            new StubCardDetector(cardCount: 1),
            new StubRectifier(),
            new StubCardIdentifier(),
            new NeverFiringTrigger(),
            settings,
            NullLoggerFactory.Instance);

        var frameProcessedCount = 0;
        pipeline.FrameProcessed += (_, _) => frameProcessedCount++;

        await pipeline.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, frameProcessedCount);
    }

    [Fact]
    public void Create_NullLoggerFactory_ThrowsArgumentNullException()
    {
        var source = new OneShotFrameSource(ArrayPool<byte>.Shared, frameCount: 1);
        var settings = new ScanSettings { ExpectedCount = 1 };

        Assert.Throws<ArgumentNullException>(() => ScanPipelineFactory.Create(
            source,
            new StubCardDetector(cardCount: 1),
            new StubRectifier(),
            new StubCardIdentifier(),
            new NeverFiringTrigger(),
            settings,
            loggers: null!));
    }

    /// A minimal finite `IFrameSource` — this package only needs to prove
    /// the factory's wiring produces a running pipeline, not exercise the
    /// pipeline's own concurrency behaviour (that is `ScanPipelineTests`'
    /// job).
    private sealed class OneShotFrameSource : IFrameSource
    {
        private readonly ArrayPool<byte> _pool;
        private readonly int _frameCount;

        public OneShotFrameSource(ArrayPool<byte> pool, int frameCount)
        {
            _pool = pool;
            _frameCount = frameCount;
            Geometry = new FrameGeometry(32, 32, RotationDegrees: 0);
        }

        public string Description => "factory-test-frame-source";

        public FrameGeometry Geometry { get; }

        public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < _frameCount; i++)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);

                const int width = 32;
                const int height = 32;
                var stride = width * 3;
                var buffer = _pool.Rent(stride * height);
                Array.Fill(buffer, (byte)1, 0, stride * height);

                yield return new CameraFrame(buffer, width, height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, _pool);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverFiringTrigger : IAutoCaptureTrigger
    {
        public bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now) => false;

        public void NotifyCaptured()
        {
        }

        public void Reset()
        {
        }
    }
}
