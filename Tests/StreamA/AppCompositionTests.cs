using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Trigger;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Unit tests for `AppComposition` — the part of A1 that is testable
/// without a window. `ComposeAsync` is the testable core: it takes every
/// dependency already built, so a counting `IFrameSourceFactory` can prove
/// the composition root opens the frame source once and starts
/// `IScanPipeline.RunAsync` exactly once, without a real folder on disk.
public class AppCompositionTests
{
    [Fact]
    public async Task ComposeAsync_OpensTheFrameSourceOnceAndStartsRunAsyncExactlyOnce()
    {
        var factory = new CountingFrameSourceFactory();
        var settings = new ScanSettings();

        var session = await AppComposition.ComposeAsync(
            factory,
            new StubCardDetector(cardCount: 0),
            new StubRectifier(),
            new StubCardIdentifier(),
            new AutoCaptureTrigger(settings),
            settings,
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        // The fake source yields zero frames, so RunAsync's loop finishes
        // immediately — awaiting it makes the assertions below reliable
        // regardless of how much of RunAsync happened synchronously.
        await session.RunTask;

        Assert.Equal(1, factory.CreateAsyncCallCount);
        Assert.Equal(1, factory.Source.ReadAsyncCallCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_FakesMode_BuildsAWorkingPipelineAgainstFolderFrameSourceFactory()
    {
        // This is the same composition path the app runs at startup
        // (CompositionMode.Fakes -> DemoFrames -> FolderFrameSourceFactory
        // -> ScanPipelineFactory.Create -> RunAsync), exercised here without
        // a window — the unit-testable half of "the app launches against
        // FolderFrameSourceFactory".
        await using var session = await AppComposition.CreateAsync(
            CompositionMode.Fakes,
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("folder:", session.Pipeline.SourceDescription, StringComparison.Ordinal);
        Assert.False(session.RunTask.IsFaulted);
    }

    /// A trivial `IFrameSource` that yields no frames and counts how many
    /// times `ReadAsync` was called — `ScanPipeline.RunAsync` calls it
    /// exactly once per `RunAsync` invocation (in the `await foreach`
    /// header, before any frame arrives), so this count is a direct proxy
    /// for "how many times was RunAsync started", without needing to spy on
    /// the pipeline itself.
    private sealed class CountingFrameSource : IFrameSource
    {
        public int ReadAsyncCallCount { get; private set; }

        public string Description => "counting-fake";

        public FrameGeometry Geometry => new(1, 1, 0);

        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct)
        {
            ReadAsyncCallCount++;
            return EmptyAsync();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<CameraFrame> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingFrameSourceFactory : IFrameSourceFactory
    {
        public CountingFrameSource Source { get; } = new();

        public int CreateAsyncCallCount { get; private set; }

        public Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct)
        {
            CreateAsyncCallCount++;
            return Task.FromResult<IFrameSource>(Source);
        }
    }
}
