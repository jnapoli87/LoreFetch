using LoreFetch.App;
using LoreFetch.App.Fakes;
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

        Assert.Equal(1, factory.Source.DisposeAsyncCallCount);
    }

    /// Orchestrator review (2026-09-21): nothing ever disposed the frame
    /// source composition opened, so `FolderFrameSource`'s own decode loop
    /// — and the temp folder it reads from — outlived every run. Proves the
    /// fix generically: `AppSession.DisposeAsync` must dispose the source
    /// exactly once, and must invoke the optional `onDisposed` cleanup hook
    /// exactly once, using a counting source rather than real file I/O.
    [Fact]
    public async Task DisposeAsync_DisposesTheFrameSourceAndRunsOnDisposedExactlyOnce()
    {
        var factory = new CountingFrameSourceFactory();
        var settings = new ScanSettings();
        var onDisposedCallCount = 0;

        var session = await AppComposition.ComposeAsync(
            factory,
            new StubCardDetector(cardCount: 0),
            new StubRectifier(),
            new StubCardIdentifier(),
            new AutoCaptureTrigger(settings),
            settings,
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken,
            onDisposed: () => onDisposedCallCount++);

        await session.RunTask;
        await session.DisposeAsync();

        Assert.Equal(1, factory.Source.DisposeAsyncCallCount);
        Assert.Equal(1, onDisposedCallCount);
    }

    /// Orchestrator review (2026-09-21): the smoke-exit leak's actual root
    /// cause. App.axaml.cs's `MaybeScheduleSmokeExit` calls
    /// `desktop.Shutdown()`, and in Avalonia 12.1's
    /// `ClassicDesktopStyleApplicationLifetime.DoShutdown`, `Shutdown()`
    /// passes `force: true`, which skips the `ShutdownRequested` invoke
    /// entirely (`if (!force) ShutdownRequested?.Invoke(...)`) — only
    /// `Exit` fires unconditionally, on every path. So App.axaml.cs now
    /// wires teardown to *both* `ShutdownRequested` (fired on a cooperative
    /// shutdown: window close, `TryShutdown()`, an OS request) and `Exit`
    /// (fired on every path, including `Shutdown()`), and on a cooperative
    /// shutdown both fire for the same session. This proves
    /// `AppSession.DisposeAsync` tolerates that — a second call is a no-op
    /// rather than double-disposing the frame source or invoking
    /// `onDisposed` twice.
    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var factory = new CountingFrameSourceFactory();
        var settings = new ScanSettings();
        var onDisposedCallCount = 0;

        var session = await AppComposition.ComposeAsync(
            factory,
            new StubCardDetector(cardCount: 0),
            new StubRectifier(),
            new StubCardIdentifier(),
            new AutoCaptureTrigger(settings),
            settings,
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken,
            onDisposed: () => onDisposedCallCount++);

        await session.RunTask;

        // Simulates ShutdownRequested and Exit both firing for the same
        // shutdown, as they do on a cooperative shutdown path.
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, factory.Source.DisposeAsyncCallCount);
        Assert.Equal(1, onDisposedCallCount);
    }

    /// The concrete regression from orchestrator review: launching in
    /// Fakes mode creates a real temp folder for `FolderFrameSourceFactory`
    /// (`DemoFrames.CreateFolder`), and before this fix nothing ever
    /// deleted it — every launch left one behind. This exercises the exact
    /// pair of methods `AppComposition` wires together, directly.
    [Fact]
    public void DemoFrames_CreateFolder_ThenDeleteFolderBestEffort_LeavesNoDirectoryBehind()
    {
        var folder = DemoFrames.CreateFolder();
        Assert.True(Directory.Exists(folder));

        DemoFrames.DeleteFolderBestEffort(folder);

        Assert.False(Directory.Exists(folder));
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
    /// A10-prep item 3: before this fix, `CreateFakesAsync` never touched
    /// `ScanSettings.GoodDistance`/`OkDistance`, so both stayed at their
    /// default of 0 and `CohortTile.ProposeFromHash` proposed `Unresolved`
    /// for every tile — the keyboard loop could capture but never commit.
    /// `DemoThresholds` is the one sanctioned place in the App that names a
    /// distance literal (see its own doc comment: LoreFetch.App.csproj is
    /// frozen and ships only `data/index/**`, and Fakes mode has no real
    /// ThresholdsFile to load), so this asserts composition actually wires
    /// those constants into the settings the pipeline was built from —
    /// non-zero, equal to `DemoThresholds`' own values, and good < ok (a
    /// swapped pair would make every hash-reachable tile Unresolved too,
    /// since a distance can never be <= a smaller "good" while > a larger
    /// "ok" — this final check would still catch a good/ok swap).
    [Fact]
    public async Task CreateAsync_FakesMode_AssignsNonZeroDemoThresholdsToScanSettings()
    {
        await using var session = await AppComposition.CreateAsync(
            CompositionMode.Fakes,
            NullLoggerFactory.Instance,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, session.Settings.GoodDistance);
        Assert.NotEqual(0, session.Settings.OkDistance);
        Assert.Equal(DemoThresholds.GoodDistance, session.Settings.GoodDistance);
        Assert.Equal(DemoThresholds.OkDistance, session.Settings.OkDistance);
        Assert.True(
            session.Settings.GoodDistance < session.Settings.OkDistance,
            "GoodDistance must be strictly less than OkDistance, or every " +
            "hash-reachable tile collapses to Unresolved.");
    }

    private sealed class CountingFrameSource : IFrameSource
    {
        public int ReadAsyncCallCount { get; private set; }

        public int DisposeAsyncCallCount { get; private set; }

        public string Description => "counting-fake";

        public FrameGeometry Geometry => new(1, 1, 0);

        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct)
        {
            ReadAsyncCallCount++;
            return EmptyAsync();
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return ValueTask.CompletedTask;
        }

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
