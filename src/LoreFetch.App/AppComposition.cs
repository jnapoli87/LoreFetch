using LoreFetch.App.Fakes;
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
    private int _disposed;

    /// <param name="pipeline">The scan pipeline. Must not be null.</param>
    /// <param name="source">The frame source. Must not be null.</param>
    /// <param name="runTask">The task returned by the one <c>RunAsync</c> call. Must not be null.</param>
    /// <param name="settings">Shared scan settings. Must not be null.</param>
    /// <param name="catalog">
    /// Oracle catalog for the "Set card manually…" type-ahead. Optional so
    /// that existing test constructors
    /// (<c>new AppSession(pipeline, source, Task.CompletedTask, settings)</c>)
    /// continue to compile without change.
    /// </param>
    /// <param name="store">
    /// Collection store for the commit path. Optional so that existing test
    /// constructors continue to compile without change. When null,
    /// <c>MainViewModel.CommitCohortAsync</c> is a no-op.
    /// </param>
    /// <param name="exporters">
    /// Registered collection exporters for the A8 export picker. Optional so
    /// that existing test constructors continue to compile without change. When
    /// null or empty, the export picker shows nothing.
    /// </param>
    /// <param name="onDisposed">Optional cleanup action run after the frame source is closed.</param>
    public AppSession(
        IScanPipeline pipeline,
        IFrameSource source,
        Task runTask,
        ScanSettings settings,
        IOracleCatalog? catalog = null,
        ICollectionStore? store = null,
        IReadOnlyList<ICollectionExporter>? exporters = null,
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
        Catalog = catalog;
        Store = store;
        Exporters = exporters;
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

    /// <summary>
    /// The oracle catalog for the "Set card manually…" type-ahead. May be
    /// null when composition does not supply one (e.g. tests that construct
    /// <see cref="AppSession"/> directly). <c>MainWindow</c> passes this to
    /// <see cref="LoreFetch.App.ViewModels.MainViewModel"/> so each tile's
    /// populator can search it.
    /// </summary>
    public IOracleCatalog? Catalog { get; }

    /// <summary>
    /// The collection store for the Space → Enter commit path. May be null
    /// when composition does not supply one. <c>MainWindow</c> passes this to
    /// <see cref="LoreFetch.App.ViewModels.MainViewModel"/>; when null,
    /// <c>CommitCohortAsync</c> is a no-op.
    /// </summary>
    public ICollectionStore? Store { get; }

    /// <summary>
    /// The registered collection exporters for the A8 export picker. May be
    /// null or empty when composition does not supply any. <c>MainWindow</c>
    /// passes this to <see cref="LoreFetch.App.ViewModels.CollectionViewModel"/>.
    /// </summary>
    public IReadOnlyList<ICollectionExporter>? Exporters { get; }

    /// Disposes the pipeline (which cancels and drains its loop), awaits the
    /// run task, then disposes the frame source itself — only at that point
    /// has `FolderFrameSource`'s own decode loop actually stopped, which is
    /// why `onDisposed` (the Fakes path's temp-folder cleanup) runs last,
    /// after the source that reads from it is closed. Exceptions the run
    /// task raises on a normal shutdown are swallowed; a caller that wants
    /// to observe a real failure should inspect `RunTask` itself before
    /// calling this.
    ///
    /// Idempotent: App.axaml.cs wires teardown to both
    /// `IClassicDesktopStyleApplicationLifetime.ShutdownRequested` and
    /// `.Exit`. On a cooperative shutdown (window close, `TryShutdown`, an
    /// OS shutdown request) both fire — `ShutdownRequested` first, then
    /// `Exit` unconditionally once teardown actually proceeds. Only
    /// `desktop.Shutdown()` (used by the smoke-exit path) skips
    /// `ShutdownRequested` entirely and goes straight to `Exit`. Guarding
    /// here, rather than trusting callers to invoke this exactly once, is
    /// what makes both hooks safe to wire without caring which one fires or
    /// how many times.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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

        var logger = loggers.CreateLogger("LoreFetch.App.AppComposition");
        var settings = new ScanSettings();

        // Item 3 (A10-prep): a fresh ScanSettings defaults GoodDistance and
        // OkDistance to 0, which makes CohortTile.ProposeFromHash propose
        // Unresolved for every tile (best.Distance <= 0 is never true) — the
        // keyboard loop could capture but never commit anything. DemoThresholds
        // is the one place in the App allowed to name a distance literal (see
        // its own doc comment for why Fakes mode can't load a real
        // ThresholdsFile); everything else derives from these two numbers.
        // Must be set before ScanPipelineFactory.Create below, so every tile
        // the pipeline constructs sees non-zero thresholds from its first
        // capture.
        settings.GoodDistance = DemoThresholds.GoodDistance;
        settings.OkDistance = DemoThresholds.OkDistance;

        // A4: when LOREFETCH_FRAMES_DIR is set, use the user's own folder
        // instead of generating a DemoFrames temp folder. A bad env var
        // (missing or empty folder) is logged and falls back gracefully —
        // the user's images are never deleted (isTempFolder stays false).
        var envVar = Environment.GetEnvironmentVariable("LOREFETCH_FRAMES_DIR");
        var (frameFolder, isTempFolder) = ChooseFrameFolder(envVar, logger);

        IFrameSourceFactory frameSourceFactory = new FolderFrameSourceFactory(frameFolder, TimeSpan.FromMilliseconds(250));

        // Item 4a (A10-prep): wrap the stub so switching the 1/3/9 selector
        // at runtime actually changes how many quads the NEXT frame yields —
        // StubCardDetector.CardCount is otherwise set once here and never
        // touched again. See LayoutFollowingCardDetector's own doc comment
        // for the threading argument (ICardDetector.Detect is only ever
        // called from ScanPipeline's single loop thread).
        ICardDetector detector = new LayoutFollowingCardDetector(
            new StubCardDetector(settings.ExpectedCount), settings);
        IRectifier rectifier = new StubRectifier();
        ICardIdentifier identifier = new StubCardIdentifier();
        IAutoCaptureTrigger trigger = new AutoCaptureTrigger(settings);

        // A6: oracle catalog for the "Set card manually…" type-ahead. The
        // StubOracleCatalog defaults to ~33,000 entries (the size where
        // AutoCompleteBox's uncapped defaults become a real problem) — see
        // plan-finding V16 and stream-a-ui.md §A5.
        IOracleCatalog catalog = new StubOracleCatalog();

        // A7: in-memory collection store — the StubCollectionStore has the
        // real dedup and commit semantics so the full Space → Enter loop
        // is exercisable against fakes before stream D's CsvCollectionStore
        // exists. Stream D replaces this with the real implementation.
        ICollectionStore store = new StubCollectionStore();

        // A8: two stub exporters — one verified, one deliberately unverified —
        // so the export picker's IsVerified badge is exercised end-to-end.
        // The real exporters (native and third-party adapters) live on stream D
        // and are wired at integration, NOT here. The UI must never name them.
        IReadOnlyList<ICollectionExporter> exporters =
        [
            new StubCollectionExporter(new ExportFormat(
                "stub-a",
                "Stub Verified Export",
                ".txt",
                IsVerified: true,
                Notes: null)),
            new StubCollectionExporter(new ExportFormat(
                "stub-b",
                "Stub Unverified Export",
                ".txt",
                IsVerified: false,
                Notes: "Not yet tested with a live tool.")),
        ];

        // Best-effort cleanup of the temp folder DemoFrames created, run
        // from AppSession.DisposeAsync only after the frame source itself
        // (and therefore its decode loop) has stopped. Orchestrator review
        // found every launch left one of these behind — nothing ever
        // deleted them. The user's own folder (isTempFolder=false) is
        // never deleted — it belongs to the user, not to this process.
        Action? onDisposed = isTempFolder
            ? () => DemoFrames.DeleteFolderBestEffort(frameFolder)
            : null;

        return ComposeAsync(
            frameSourceFactory,
            detector,
            rectifier,
            identifier,
            trigger,
            settings,
            loggers,
            ct,
            catalog: catalog,
            store: store,
            exporters: exporters,
            onDisposed: onDisposed);
    }

    /// <summary>
    /// Selects the frame-source folder for the Fakes composition path.
    /// </summary>
    /// <param name="envVar">
    /// The raw value of <c>LOREFETCH_FRAMES_DIR</c>, or <c>null</c> /
    /// empty if the variable is not set.
    /// </param>
    /// <param name="logger">
    /// Receives an error when the env var is set but invalid, so the
    /// operator sees exactly what went wrong without a crash or an exception
    /// silently swallowed higher up.
    /// </param>
    /// <returns>
    /// <c>(folder, isTempFolder)</c>: the chosen folder path plus a flag
    /// that is <c>true</c> only for a DemoFrames temp folder that this
    /// process created and should delete on teardown. A user-supplied
    /// folder always yields <c>false</c> — never delete the user's data.
    /// </returns>
    /// <remarks>
    /// Factored out of <see cref="CreateFakesAsync"/> so
    /// <c>Tests/StreamA</c> can exercise the three code paths — user
    /// folder with images, user folder with no images, and env var unset —
    /// without launching the full composition stack.
    /// </remarks>
    internal static (string folder, bool isTempFolder) ChooseFrameFolder(
        string? envVar, ILogger logger)
    {
        if (!string.IsNullOrEmpty(envVar))
        {
            if (!Directory.Exists(envVar))
            {
                logger.LogError(
                    "LOREFETCH_FRAMES_DIR is set to '{Path}' but that directory does not exist. " +
                    "Falling back to generated demo frames.",
                    envVar);
            }
            else
            {
                var hasImages = Directory.EnumerateFiles(envVar)
                    .Any(IsImageFile);

                if (!hasImages)
                {
                    logger.LogError(
                        "LOREFETCH_FRAMES_DIR is set to '{Path}' but it contains no " +
                        "image files (.png, .jpg, .jpeg, .bmp). " +
                        "Falling back to generated demo frames.",
                        envVar);
                }
                else
                {
                    // User-supplied folder, looks good — use it as-is and
                    // leave cleanup entirely to the user.
                    return (envVar, isTempFolder: false);
                }
            }
        }

        // Env var was absent, empty, or invalid — generate the demo frames.
        return (DemoFrames.CreateFolder(), isTempFolder: true);
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
        IOracleCatalog? catalog = null,
        ICollectionStore? store = null,
        IReadOnlyList<ICollectionExporter>? exporters = null,
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

        return new AppSession(pipeline, source, runTask, settings,
            catalog: catalog, store: store, exporters: exporters, onDisposed: onDisposed);
    }

    // A4: image extensions that FolderFrameSource can decode. Must stay in
    // sync with FolderFrameSource.ImageExtensions (that field is private).
    // ".png", ".jpg", ".jpeg", ".bmp" — same four that FolderFrameSource uses.
    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).AsSpan().Equals(".png", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).AsSpan().Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).AsSpan().Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).AsSpan().Equals(".bmp", StringComparison.OrdinalIgnoreCase);
}
