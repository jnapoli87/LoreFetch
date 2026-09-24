using LoreFetch.App.Diagnostics;
using LoreFetch.App.Fakes;
using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using LoreFetch.Core.Export;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Scanning;
using LoreFetch.Core.Trigger;
using Microsoft.Extensions.Logging;


namespace LoreFetch.App;

/// Which set of dependencies the composition root wires up. `Fakes` is all
/// this stream builds — the seven fakes in `Core/Fakes` plus the
/// `AutoCaptureTrigger` this stream owns — so the app runs end-to-end with
/// no camera, no hash index and no collection store.
///
/// `Real` (packages I1–I3) wires every piece for real: the collection store
/// (`LoreFetch.Core.Collection.CsvCollectionStore`) and both export adapters
/// (`LoreFetch.Core.Export.NativeCsvExporter`, `LoreFetch.Core.Export.MoxfieldCsvExporter`,
/// package I1); the detector, rectifier, and a single
/// `LoreFetch.Core.Identification.HashCardIdentifier` used as BOTH
/// `ICardIdentifier` and `IOracleCatalog`, loaded from the COMMITTED index
/// (`DataFiles.IndexPath`), with `ScanSettings.GoodDistance`/`OkDistance` fed
/// from the committed `ThresholdsFile` (`DataFiles.ThresholdsPath`) rather
/// than any literal in this file (package I2); and the frame source —
/// `LoreFetch.Capture.WebcamFrameSourceFactory` by default, or a folder of
/// images when `LOREFETCH_FRAMES_DIR` is set, so real captured frames can be
/// replayed through the full real pipeline without a camera (package I3).
/// `ScanSettings.CameraRotationDegrees` is set to 0 at composition — the
/// camera is landscape and unrotated (CLAUDE.md "Geometry"); the contract's
/// own default stays 90 and is unchanged.
///
/// A missing or corrupt committed index/thresholds file fails Real mode
/// LOUDLY (an uncaught, logged exception — see <see cref="CreateRealAsync"/>)
/// rather than falling back to Fakes: a shipped app that quietly runs on
/// stub identification is worse than one that refuses to start. A missing
/// camera at startup is a DIFFERENT case and does not crash — see
/// <see cref="RealWebcamFrameSourceFactory"/>.
///
/// Selected by `LOREFETCH_MODE` (`fakes` | `real`, case-insensitive; see
/// <see cref="ResolveCompositionMode"/>). Package I3 flips the default to
/// `Real` — unset now means Real, not Fakes; `fakes` must still be passed
/// explicitly to get the old behaviour.
public enum CompositionMode
{
    Fakes,
    Real,
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
    /// <param name="diagnostics">
    /// A10's preview/pipeline fps counters. Optional so that existing test
    /// constructors continue to compile without change. When null,
    /// <c>MainWindow</c> simply does not record any frame counts.
    /// </param>
    /// <param name="reporter">
    /// The background loop that logs <paramref name="diagnostics"/> every
    /// 10 s. Optional; disposed (if present) as part of
    /// <see cref="DisposeAsync"/>. Composition owns starting it — never
    /// pass one without also passing the <paramref name="diagnostics"/> it
    /// reads from.
    /// </param>
    public AppSession(
        IScanPipeline pipeline,
        IFrameSource source,
        Task runTask,
        ScanSettings settings,
        IOracleCatalog? catalog = null,
        ICollectionStore? store = null,
        IReadOnlyList<ICollectionExporter>? exporters = null,
        Action? onDisposed = null,
        PreviewDiagnostics? diagnostics = null,
        DiagnosticsReporter? reporter = null)
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
        Diagnostics = diagnostics;
        _reporter = reporter;
    }

    private readonly DiagnosticsReporter? _reporter;

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

    /// <summary>
    /// A10's preview/pipeline fps counters. <c>MainWindow</c> records into
    /// this from <c>OnFrameProcessed</c> (pipeline frames) and
    /// <c>OnRenderFrame</c> (frames actually blitted). May be null when
    /// composition does not supply one (e.g. tests that construct
    /// <see cref="AppSession"/> directly) — <c>MainWindow</c> treats that as
    /// "don't record".
    /// </summary>
    public PreviewDiagnostics? Diagnostics { get; }

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

        if (_reporter is not null)
        {
            await _reporter.DisposeAsync().ConfigureAwait(false);
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
/// `Fakes` mode builds every pipeline piece against `Core/Fakes`; `Real`
/// mode (packages I1–I3) builds every piece for real, over the committed
/// index and thresholds file — nothing in `Real`'s own path ever names a
/// distance literal (I4; `DemoThresholds` is the sanctioned Fakes-only
/// exception).
public static class AppComposition
{
    public static Task<AppSession> CreateAsync(CompositionMode mode, ILoggerFactory loggers, CancellationToken ct) =>
        mode switch
        {
            CompositionMode.Fakes => CreateFakesAsync(loggers, ct),
            CompositionMode.Real => CreateRealAsync(loggers, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported composition mode."),
        };

    /// <summary>
    /// Resolves <c>LOREFETCH_MODE</c> into a <see cref="CompositionMode"/>.
    /// Takes the raw env var value as a parameter, rather than reading
    /// <see cref="Environment.GetEnvironmentVariable"/> itself, so the
    /// resolution logic is a pure function `Tests/App` can exercise
    /// without mutating process-global state (the same shape as
    /// <see cref="ChooseFrameFolder"/>).
    /// </summary>
    /// <param name="envVar">
    /// The raw value of <c>LOREFETCH_MODE</c>, or <c>null</c>/empty/whitespace
    /// if unset. Matched case-insensitively against <c>"fakes"</c> and
    /// <c>"real"</c>.
    /// </param>
    /// <param name="logger">Receives an error when the value is neither.</param>
    /// <returns>
    /// <see cref="CompositionMode.Real"/> when unset or blank — package I3's
    /// default flip, now that the camera is wired; an explicit <c>"fakes"</c>
    /// still selects <see cref="CompositionMode.Fakes"/>, and an explicit
    /// <c>"real"</c> selects <see cref="CompositionMode.Real"/>, both
    /// case-insensitively. An UNRECOGNISED value still falls back to
    /// <see cref="CompositionMode.Fakes"/> — the one mode that can never fail
    /// to start — logging an error, rather than to the new Real default:
    /// a typo must not be the reason the app tries (and possibly fails) to
    /// open a camera or load a hash index. Never throws.
    /// </returns>
    internal static CompositionMode ResolveCompositionMode(string? envVar, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(envVar))
        {
            return CompositionMode.Real;
        }

        if (string.Equals(envVar, "fakes", StringComparison.OrdinalIgnoreCase))
        {
            return CompositionMode.Fakes;
        }

        if (string.Equals(envVar, "real", StringComparison.OrdinalIgnoreCase))
        {
            return CompositionMode.Real;
        }

        logger.LogError(
            "LOREFETCH_MODE is set to '{Value}', which is neither 'fakes' nor 'real'. Falling back to Fakes.",
            envVar);
        return CompositionMode.Fakes;
    }

    private static Task<AppSession> CreateFakesAsync(ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger("LoreFetch.App.AppComposition");
        var settings = CreateSettingsWithDemoThresholds();
        var pieces = BuildFakesPipelinePieces(settings, logger);

        // A7: in-memory collection store — the StubCollectionStore has the
        // real dedup and commit semantics so the full Space → Enter loop
        // is exercisable against fakes before stream D's CsvCollectionStore
        // exists. Real mode (I1) uses CsvCollectionStore instead.
        ICollectionStore store = new StubCollectionStore();

        // A8: two stub exporters — one verified, one deliberately unverified —
        // so the export picker's IsVerified badge is exercised end-to-end.
        // The real exporters (native and third-party adapters) live on stream D
        // and are wired in Real mode (I1) instead — the UI must never name them.
        IReadOnlyList<ICollectionExporter> exporters = CreateStubExporters();

        return ComposeAsync(
            pieces.FrameSourceFactory,
            pieces.Detector,
            pieces.Rectifier,
            pieces.Identifier,
            pieces.Trigger,
            settings,
            loggers,
            ct,
            catalog: pieces.Catalog,
            store: store,
            exporters: exporters,
            onDisposed: pieces.OnDisposed);
    }

    /// <summary>
    /// Packages I1–I3: the full Real composition path. Every piece is real:
    /// <see cref="ContourCardDetector"/>, <see cref="PerspectiveRectifier"/>,
    /// one <see cref="HashCardIdentifier"/> loaded from the committed index
    /// and used as both <see cref="ICardIdentifier"/> and
    /// <see cref="IOracleCatalog"/>, thresholds loaded from the committed
    /// <see cref="ThresholdsFile"/>, the real collection store and both real
    /// export adapters (package I1), and a frame source that is either the
    /// real webcam or a replayed folder of frames (package I3). NO distance
    /// literal appears anywhere in this method — see
    /// <see cref="ThresholdsFile"/>-derived <c>settings.GoodDistance</c>/
    /// <c>OkDistance</c> below; <see cref="DemoThresholds"/> is Fakes-mode
    /// only.
    ///
    /// A missing or corrupt index/thresholds file is a LOUD failure: this
    /// method lets <see cref="HashCardIdentifier.Load"/>/
    /// <see cref="ThresholdsFile.Load"/>'s own exceptions propagate — after
    /// logging them at <see cref="LogLevel.Critical"/>, naming the path —
    /// rather than catching and falling back to Fakes (docs/history/orchestration-plan.md's
    /// I2/I3 override: "a shipped app that quietly runs on stub identification
    /// is worse than one that refuses to start"). <see cref="App"/> is what
    /// turns that exception into a clean, non-zero exit.
    /// </summary>
    private static Task<AppSession> CreateRealAsync(ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger("LoreFetch.App.AppComposition");

        var identifier = LoadRealIdentifier(loggers, logger);
        var thresholds = LoadRealThresholds(logger);

        var settings = new ScanSettings
        {
            GoodDistance = thresholds.GoodDistance,
            OkDistance = thresholds.OkDistance,
        };

        // Geometry re-ruling (CLAUDE.md "Geometry"): the camera stays
        // landscape and unrotated for every layout. ScanSettings' own
        // default stays 90 (frozen contract) — this is a value set at
        // composition, not a change to that default.
        settings.CameraRotationDegrees = 0;
        logger.LogInformation("Real mode camera rotation: {RotationDegrees} degrees.", settings.CameraRotationDegrees);

        ICardDetector detector = new ContourCardDetector(loggers.CreateLogger<ContourCardDetector>());
        IRectifier rectifier = new PerspectiveRectifier();
        IAutoCaptureTrigger trigger = new AutoCaptureTrigger(settings);

        var (frameSourceFactory, onDisposed) = ChooseRealFrameSourceFactory(
            Environment.GetEnvironmentVariable("LOREFETCH_FRAMES_DIR"), loggers, logger);

        var collectionPath = ResolveCollectionPath(
            Environment.GetEnvironmentVariable("LOREFETCH_COLLECTION"), logger);

        ICollectionStore store = new CsvCollectionStore(collectionPath, loggers.CreateLogger<CsvCollectionStore>());

        // Both v1 export adapters (CONTRACTS.md: "v1 ships exactly two
        // formats"), in the same order the Fakes-mode stub pair models
        // (one native/verified, one third-party/verified) — see
        // NativeCsvExporter/MoxfieldCsvExporter's own doc comments for why
        // each is IsVerified: true.
        IReadOnlyList<ICollectionExporter> exporters =
        [
            new NativeCsvExporter(),
            new MoxfieldCsvExporter(),
        ];

        return ComposeAsync(
            frameSourceFactory,
            detector,
            rectifier,
            identifier,
            trigger,
            settings,
            loggers,
            ct,
            catalog: identifier,
            store: store,
            exporters: exporters,
            onDisposed: onDisposed);
    }

    /// Loads the committed hash index, used as BOTH <see cref="ICardIdentifier"/>
    /// and <see cref="IOracleCatalog"/> (one instance, per the I2 override —
    /// a second load would risk the two seams silently disagreeing about
    /// which index they read). Logs at <see cref="LogLevel.Critical"/>,
    /// naming <see cref="DataFiles.IndexPath"/>, before letting the
    /// underlying exception (<see cref="FileNotFoundException"/> for a
    /// missing file, <see cref="LoreFetch.Core.Identification.HashIndexFormatException"/>
    /// for a corrupt one) propagate — never caught into a Fakes fallback.
    private static HashCardIdentifier LoadRealIdentifier(ILoggerFactory loggers, ILogger logger)
    {
        try
        {
            return HashCardIdentifier.Load(DataFiles.IndexPath, loggers);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Real mode: failed to load the hash index from \"{Path}\". Refusing to start rather than " +
                "silently running with stub identification.",
                DataFiles.IndexPath);
            throw;
        }
    }

    /// Loads the committed thresholds file. Same loud-failure shape as
    /// <see cref="LoadRealIdentifier"/> — <see cref="ThresholdsFile.Load"/>
    /// already throws on a missing file, an unsupported format version, or
    /// a missing/malformed field (its own doc comment: "loud rather than
    /// lenient"); this only adds the named-path critical log before letting
    /// that exception propagate.
    private static ThresholdsFile LoadRealThresholds(ILogger logger)
    {
        try
        {
            var thresholds = ThresholdsFile.Load(DataFiles.ThresholdsPath);
            logger.LogInformation(
                "Loaded thresholds from {Path}: goodDistance={GoodDistance} okDistance={OkDistance}.",
                DataFiles.ThresholdsPath, thresholds.GoodDistance, thresholds.OkDistance);
            return thresholds;
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Real mode: failed to load thresholds from \"{Path}\". Refusing to start rather than " +
                "silently running with stub identification.",
                DataFiles.ThresholdsPath);
            throw;
        }
    }

    /// <summary>
    /// Package I3's frame-source switch for Real mode. <c>LOREFETCH_FRAMES_DIR</c>,
    /// when set and valid, replays a folder of images through the REAL
    /// detector/rectifier/identifier — never opening the camera, which is
    /// how real captured frames get exercised through the full real
    /// pipeline without hardware (and how this package's own smoke tests
    /// and CI reach Real mode at all: the C920 stays free for fixture
    /// capture). An invalid value (missing directory, or one with no
    /// decodable image) falls back exactly the way <see cref="ChooseFrameFolder"/>
    /// already does for Fakes mode — same log line, same generated
    /// demo-frames fallback — per the I2/I3 override ("same missing/empty-folder
    /// handling as Fakes mode"); it is unusual to pair "Real" identification
    /// with placeholder frames, but this is a deliberate, documented
    /// degrade-in-place rather than a crash.
    ///
    /// Unset entirely: the real webcam
    /// (<see cref="LoreFetch.Capture.WebcamFrameSourceFactory"/>), wrapped in
    /// <see cref="RealWebcamFrameSourceFactory"/> so "no camera attached"
    /// fails through the pipeline's existing <c>SourceFailed</c> path
    /// instead of crashing composition — see that type's own doc comment.
    /// </summary>
    private static (IFrameSourceFactory Factory, Action? OnDisposed) ChooseRealFrameSourceFactory(
        string? envVar, ILoggerFactory loggers, ILogger logger)
    {
        if (string.IsNullOrEmpty(envVar))
        {
            IFrameSourceFactory webcamFactory = new RealWebcamFrameSourceFactory(
                new WebcamFrameSourceFactory(loggers), loggers.CreateLogger<RealWebcamFrameSourceFactory>());
            return (webcamFactory, null);
        }

        var (folder, isTempFolder) = ChooseFrameFolder(envVar, logger);
        IFrameSourceFactory folderFactory = new FolderFrameSourceFactory(folder, TimeSpan.FromMilliseconds(250));
        Action? onDisposed = isTempFolder ? () => DemoFrames.DeleteFolderBestEffort(folder) : null;
        return (folderFactory, onDisposed);
    }

    /// <summary>
    /// Wraps the real <see cref="LoreFetch.Capture.WebcamFrameSourceFactory"/>
    /// so a <see cref="FrameSourceException"/> at <c>CreateAsync</c> time —
    /// "nothing usable is found" (that type's own doc comment): no camera
    /// attached, or none matching the required format — does not crash
    /// composition before <see cref="MainWindow"/> even exists. That
    /// exception is instead deferred: this factory returns an
    /// already-failed <see cref="IFrameSource"/> whose <c>ReadAsync</c>
    /// throws the SAME exception the moment <see cref="IScanPipeline.RunAsync"/>
    /// evaluates it — which is textually inside that method's own
    /// <c>try</c> block (CONTRACTS.md: "SourceFailed is raised ... immediately
    /// before RunAsync faults"), so the existing mechanism fires exactly as
    /// it would for a camera that disconnects mid-session, and
    /// <see cref="MainWindow"/> renders the same banner it already renders
    /// for that case. <see cref="LoreFetch.Capture.WebcamFrameSourceFactory"/>
    /// itself is frozen Capture production code this package must not edit
    /// — this is composition-root-only behaviour.
    /// </summary>
    private sealed class RealWebcamFrameSourceFactory : IFrameSourceFactory
    {
        private readonly IFrameSourceFactory _inner;
        private readonly ILogger _logger;

        public RealWebcamFrameSourceFactory(IFrameSourceFactory inner, ILogger logger)
        {
            _inner = inner;
            _logger = logger;
        }

        public async Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct)
        {
            try
            {
                return await _inner.CreateAsync(settings, ct).ConfigureAwait(false);
            }
            catch (FrameSourceException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Real mode: no usable camera found at startup ({Message}). The preview will show this " +
                    "failure via the normal SourceFailed banner instead of crashing.",
                    ex.Message);
                return new AlreadyFailedFrameSource(ex);
            }
        }
    }

    /// An <see cref="IFrameSource"/> that never delivers a frame: its
    /// <c>ReadAsync</c> throws the captured <see cref="FrameSourceException"/>
    /// synchronously, the moment it is called — see
    /// <see cref="RealWebcamFrameSourceFactory"/>'s own doc comment for why
    /// that specific timing matters.
    private sealed class AlreadyFailedFrameSource : IFrameSource
    {
        private readonly FrameSourceException _exception;

        public AlreadyFailedFrameSource(FrameSourceException exception)
        {
            _exception = exception;
        }

        public string Description => $"camera unavailable: {_exception.Message}";

        public FrameGeometry Geometry => new(1, 1, RotationDegrees: 0);

        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct) => throw _exception;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves the collection file's path. Takes the raw env var value as a
    /// parameter (see <see cref="ResolveCompositionMode"/> for why), so
    /// <c>Tests/App</c> can exercise the whole resolution — including the
    /// "create the directory if missing" step — against a temp path, never the
    /// real Documents folder.
    /// </summary>
    /// <param name="envVar">
    /// The raw value of <c>LOREFETCH_COLLECTION</c> (a full file path), or
    /// <c>null</c>/empty/whitespace if unset.
    /// </param>
    /// <param name="logger">Receives an informational line naming the resolved path.</param>
    /// <returns>
    /// <paramref name="envVar"/>, fully qualified, when set; otherwise
    /// <c>&lt;Environment.SpecialFolder.MyDocuments&gt;/LoreFetch/collection.csv</c>.
    /// Either way, the containing directory is created first if it does not
    /// already exist — <see cref="Directory.CreateDirectory"/> is a no-op when
    /// it does — so <see cref="CsvCollectionStore"/> never has to.
    /// </returns>
    internal static string ResolveCollectionPath(string? envVar, ILogger logger)
    {
        var path = string.IsNullOrWhiteSpace(envVar)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LoreFetch", "collection.csv")
            : envVar;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        logger.LogInformation("Real mode collection file: {Path}", fullPath);
        return fullPath;
    }

    private static ScanSettings CreateSettingsWithDemoThresholds()
    {
        var settings = new ScanSettings();

        // Item 3 (A10-prep): a fresh ScanSettings defaults GoodDistance and
        // OkDistance to 0, which makes CohortTile.ProposeFromHash propose
        // Unresolved for every tile (best.Distance <= 0 is never true) — the
        // keyboard loop could capture but never commit anything. DemoThresholds
        // is the one place in the App allowed to name a distance literal (see
        // its own doc comment for why neither mode can load a real
        // ThresholdsFile yet — that is I2's job); everything else derives
        // from these two numbers. Must be set before ScanPipelineFactory.Create
        // runs, so every tile the pipeline constructs sees non-zero thresholds
        // from its first capture.
        settings.GoodDistance = DemoThresholds.GoodDistance;
        settings.OkDistance = DemoThresholds.OkDistance;
        return settings;
    }

    /// <summary>
    /// Builds Fakes mode's pipeline pieces — the frame source, detector,
    /// rectifier, identifier and oracle catalog, all backed by
    /// <c>Core/Fakes</c>. Package I2/I3 gave <see cref="CompositionMode.Real"/>
    /// its own, entirely real wiring (see <see cref="CreateRealAsync"/>);
    /// this helper is Fakes-only now.
    /// </summary>
    private static PipelinePieces BuildFakesPipelinePieces(ScanSettings settings, ILogger logger)
    {
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
        // Item 4b (A10-prep): wrap the stub so a captured cohort shows every
        // hash-reachable tile state (confident / low-confidence / Unresolved)
        // instead of every tile landing on the same one — see
        // DemoCardIdentifier's own doc comment for the distance derivation,
        // the concurrency argument and why per-capture oracle ids are skipped.
        ICardIdentifier identifier = new DemoCardIdentifier(new StubCardIdentifier(), settings);
        IAutoCaptureTrigger trigger = new AutoCaptureTrigger(settings);

        // A6: oracle catalog for the "Set card manually…" type-ahead. The
        // StubOracleCatalog defaults to ~33,000 entries (the size where
        // AutoCompleteBox's uncapped defaults become a real problem) — see
        // plan-finding V16 and docs/design/app.md §A5.
        IOracleCatalog catalog = new StubOracleCatalog();

        // Best-effort cleanup of the temp folder DemoFrames created, run
        // from AppSession.DisposeAsync only after the frame source itself
        // (and therefore its decode loop) has stopped. Orchestrator review
        // found every launch left one of these behind — nothing ever
        // deleted them. The user's own folder (isTempFolder=false) is
        // never deleted — it belongs to the user, not to this process.
        Action? onDisposed = isTempFolder
            ? () => DemoFrames.DeleteFolderBestEffort(frameFolder)
            : null;

        return new PipelinePieces(frameSourceFactory, detector, rectifier, identifier, trigger, catalog, onDisposed);
    }

    private static IReadOnlyList<ICollectionExporter> CreateStubExporters() =>
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

    /// The pipeline-facing dependencies Fakes mode builds (see
    /// <see cref="BuildFakesPipelinePieces"/>). Real mode builds its own,
    /// entirely real set inline in <see cref="CreateRealAsync"/>.
    private readonly record struct PipelinePieces(
        IFrameSourceFactory FrameSourceFactory,
        ICardDetector Detector,
        IRectifier Rectifier,
        ICardIdentifier Identifier,
        IAutoCaptureTrigger Trigger,
        IOracleCatalog Catalog,
        Action? OnDisposed);

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
    /// Factored out of <see cref="BuildFakesPipelinePieces"/> so
    /// <c>Tests/App</c> can exercise the three code paths — user
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

    /// The testable core of composition, factored out so Tests/App can
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

        // Item 5 (A10-prep): start the A10 diagnostics loop alongside the
        // pipeline, for every composed session — MainWindow records into
        // `diagnostics` (frames processed / frames actually rendered) and
        // `reporter` logs both, plus memory and GC counters, every 10 s.
        // AppSession.DisposeAsync stops it.
        var diagnostics = new PreviewDiagnostics(DateTimeOffset.UtcNow);
        var reporter = new DiagnosticsReporter(diagnostics, loggers.CreateLogger("LoreFetch.App.Diagnostics"));

        return new AppSession(pipeline, source, runTask, settings,
            catalog: catalog, store: store, exporters: exporters, onDisposed: onDisposed,
            diagnostics: diagnostics, reporter: reporter);
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
