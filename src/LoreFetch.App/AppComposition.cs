using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using LoreFetch.Core.Trigger;
using Microsoft.Extensions.Logging;

namespace LoreFetch.App;

/// Which set of dependencies the composition root wires up. `Fakes` is all
/// this stream builds — the seven fakes in `Core/Fakes` plus the
/// `AutoCaptureTrigger` this stream owns — so the app runs end-to-end with
/// no camera, no hash index and no collection store. `Real` (the webcam via
/// `LoreFetch.Capture`, the hash index, the CSV store) is added at
/// integration once streams B, C and D land; this enum exists now precisely
/// so nothing about `AppComposition`'s shape has to change when that case
/// is added — a caller already switches on `CompositionMode`.
public enum CompositionMode
{
    Fakes,
}

/// Everything the app needs once composition is done: the pipeline, the
/// already-started `RunAsync` task (started exactly once, inside
/// `AppComposition` — nothing else may call it), and the settings it was
/// built from. `DisposeAsync` is the one thing a caller needs to call to
/// tear the whole session down.
public sealed class AppSession : IAsyncDisposable
{
    private readonly Action? _onDisposed;

    public AppSession(
        IScanPipeline pipeline,
        IFrameSource source,
        Task runTask,
        ScanSettings settings,
        Action? onDisposed = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runTask);
        ArgumentNullException.ThrowIfNull(settings);

        Pipeline = pipeline;
        Source = source;
        RunTask = runTask;
        Settings = settings;
        _onDisposed = onDisposed;
    }

    public IScanPipeline Pipeline { get; }

    /// The frame source composition opened. `ScanPipeline` never disposes
    /// this — its own doc comment says so explicitly ("does not dispose the
    /// underlying IFrameSource — the pipeline does not own its lifecycle")
    /// — so whoever opened it is responsible for closing it. Orchestrator
    /// review (2026-09-21) found that nothing ever did: `FolderFrameSource`
    /// keeps decoding on its own timer until its `DisposeAsync` is called,
    /// so the demo path's background loop, and the temp folder it reads
    /// from, outlived every run.
    public IFrameSource Source { get; }

    /// The `Task` returned by the one `RunAsync` call composition made.
    /// Faults when the frame source fails — CONTRACTS.md: `SourceFailed` is
    /// "raised on the pipeline's background thread immediately before
    /// RunAsync faults". Rendering that as a failure state is A8; for now
    /// this is what a smoke test (or a unit test) awaits, or inspects, to
    /// prove the loop is alive rather than silently never started.
    public Task RunTask { get; }

    public ScanSettings Settings { get; }

    /// Disposes the pipeline (which cancels and drains its loop), awaits the
    /// run task, then disposes the frame source itself — only at that point
    /// has `FolderFrameSource`'s own decode loop actually stopped, which is
    /// why `onDisposed` (the Fakes path's temp-folder cleanup) runs last,
    /// after the source that reads from it is closed. Exceptions the run
    /// task raises on a normal shutdown are swallowed; a caller that wants
    /// to observe a real failure should inspect `RunTask` itself before
    /// calling this.
    public async ValueTask DisposeAsync()
    {
        await Pipeline.DisposeAsync().ConfigureAwait(false);

        try
        {
            await RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (FrameSourceException)
        {
        }

        await Source.DisposeAsync().ConfigureAwait(false);

        _onDisposed?.Invoke();
    }
}

/// The app's composition root (CONTRACTS.md "Composition": "the App is the
/// composition root, but it does not know the wiring"). It only ever calls
/// the two frozen entry points — `IFrameSourceFactory.CreateAsync` and
/// `ScanPipelineFactory.Create` — then starts `RunAsync` exactly once.
/// Global A override: built against fakes only; nothing here ever compares
/// a distance.
public static class AppComposition
{
    public static Task<AppSession> CreateAsync(CompositionMode mode, ILoggerFactory loggers, CancellationToken ct) =>
        mode switch
        {
            CompositionMode.Fakes => CreateFakesAsync(loggers, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported composition mode."),
        };

    private static Task<AppSession> CreateFakesAsync(ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var settings = new ScanSettings();

        var frameFolder = DemoFrames.CreateFolder();
        IFrameSourceFactory frameSourceFactory = new FolderFrameSourceFactory(frameFolder, TimeSpan.FromMilliseconds(250));

        ICardDetector detector = new StubCardDetector(settings.ExpectedCount);
        IRectifier rectifier = new StubRectifier();
        ICardIdentifier identifier = new StubCardIdentifier();
        IAutoCaptureTrigger trigger = new AutoCaptureTrigger(settings);

        // Best-effort cleanup of the temp folder DemoFrames created, run
        // from AppSession.DisposeAsync only after the frame source itself
        // (and therefore its decode loop) has stopped. Orchestrator review
        // found every launch left one of these behind — nothing ever
        // deleted them.
        return ComposeAsync(
            frameSourceFactory,
            detector,
            rectifier,
            identifier,
            trigger,
            settings,
            loggers,
            ct,
            onDisposed: () => DemoFrames.DeleteFolderBestEffort(frameFolder));
    }

    /// The testable core of composition, factored out so Tests/StreamA can
    /// substitute a counting `IFrameSourceFactory` (and trivial detector /
    /// rectifier / identifier / trigger doubles) without touching a real
    /// folder on disk or the real fakes. Opens the frame source, builds the
    /// pipeline through the frozen `ScanPipelineFactory`, and starts
    /// `RunAsync` exactly once — this is the one place in the app allowed
    /// to call it. `onDisposed` is an optional extra cleanup step (e.g. a
    /// temp folder the caller created for this frame source) run at the end
    /// of `AppSession.DisposeAsync`, after the source itself is closed.
    public static async Task<AppSession> ComposeAsync(
        IFrameSourceFactory frameSourceFactory,
        ICardDetector detector,
        IRectifier rectifier,
        ICardIdentifier identifier,
        IAutoCaptureTrigger trigger,
        ScanSettings settings,
        ILoggerFactory loggers,
        CancellationToken ct,
        Action? onDisposed = null)
    {
        ArgumentNullException.ThrowIfNull(frameSourceFactory);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(rectifier);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggers);

        var source = await frameSourceFactory.CreateAsync(settings, ct).ConfigureAwait(false);
        var pipeline = ScanPipelineFactory.Create(source, detector, rectifier, identifier, trigger, settings, loggers);
        var runTask = pipeline.RunAsync(ct);

        return new AppSession(pipeline, source, runTask, settings, onDisposed);
    }
}
