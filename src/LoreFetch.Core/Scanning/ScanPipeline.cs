using System.Diagnostics;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoreFetch.Core.Scanning;

/// The implementation of <see cref="IScanPipeline"/>. Public so tests can
/// construct it directly — an `InternalsVisibleTo` would mean editing a
/// frozen `.csproj` — but callers outside tests should prefer
/// `ScanPipelineFactory.Create` (package S0.4b), which owns the wiring
/// recipe and loads stream B's thresholds file into `ScanSettings`.
///
/// This is the highest-risk type in the foundation: it is frozen at the
/// fork, all four streams compose through it, and its bugs are lifetime and
/// threading bugs, not logic bugs — see the retained-frame handoff below.
public sealed class ScanPipeline : IScanPipeline
{
    /// Detection always asks for the maximum layout, regardless of
    /// `ScanSettings.ExpectedCount` (orchestration finding V12). Space
    /// captures whatever is on the table, so detection must always see the
    /// maximum; the TRIGGER is what compares the count against
    /// `ExpectedCount`, not the call into `ICardDetector`.
    public const int MaxDetectionCards = 9;

    /// How many ranked candidates each tile carries. Not specified by the
    /// contract — five is enough to back the right-click runner-up menu
    /// without paying to carry tail entries nothing consumes.
    private const int CandidatesPerTile = 5;

    private readonly IFrameSource _source;
    private readonly ICardDetector _detector;
    private readonly IRectifier _rectifier;
    private readonly ICardIdentifier _identifier;
    private readonly IAutoCaptureTrigger _trigger;
    private readonly ScanSettings _settings;
    private readonly ILogger _logger;
    private readonly FrameGeometry _geometry;

    private readonly CancellationTokenSource _stopCts = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DateTimeOffset _startWallClock = DateTimeOffset.UtcNow;

    // Guards the retained frame/snapshot pair. The pipeline's own loop
    // thread replaces and disposes them on every frame; CaptureAsync takes
    // ownership of them from any thread. Both go through TryCaptureFromRetained,
    // which is the one place the pair is ever read or cleared.
    private readonly object _retainedLock = new();
    private CameraFrame? _retainedFrame;
    private DetectionSnapshot? _latestSnapshot;

    // IAutoCaptureTrigger carries no thread-safety guarantee of its own, and
    // it is called both from the loop thread (Evaluate/NotifyCaptured) and
    // from CaptureAsync's background thread (NotifyCaptured) — the same
    // class of hazard the retained-frame lock exists for, one layer up.
    private readonly object _triggerLock = new();

    private int _disposed;

    /// Prefer `ScanPipelineFactory.Create`. This constructor exists so tests
    /// can build a pipeline directly against the fakes.
    public ScanPipeline(
        IFrameSource source,
        ICardDetector detector,
        IRectifier rectifier,
        ICardIdentifier identifier,
        IAutoCaptureTrigger trigger,
        ScanSettings settings,
        ILogger<ScanPipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(rectifier);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(settings);

        _source = source;
        _detector = detector;
        _rectifier = rectifier;
        _identifier = identifier;
        _trigger = trigger;
        _settings = settings;
        _logger = logger ?? NullLogger<ScanPipeline>.Instance;

        // Captured once, like SourceDescription below: both are fixed at
        // source-open time, so nothing downstream needs to keep touching the
        // IFrameSource for them.
        _geometry = source.Geometry;
        SourceDescription = source.Description;
    }

    public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;

    public event Action<Cohort>? AutoCaptured;

    public event Action<FrameSourceException>? SourceFailed;

    public string SourceDescription { get; }

    /// Safe to call from any thread, concurrently with RunAsync's loop.
    public Task<Cohort?> CaptureAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Offloaded: up to nine rectify+identify passes is too much to run
        // inline on whatever thread called this (typically the UI thread).
        return Task.Run(
            () =>
            {
                var cohort = TryCaptureFromRetained(CaptureReason.Manual, ct);
                if (cohort is not null)
                {
                    // A real capture happened, so the trigger must hear
                    // about it — manual included — so the settle re-arm
                    // rule applies and a static tableau can't re-fire on its
                    // own. Zero detections is a documented no-op (nothing
                    // was captured), so it does not notify.
                    lock (_triggerLock)
                    {
                        _trigger.NotifyCaptured();
                    }
                }

                return cohort;
            },
            ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);

        try
        {
            await foreach (var frame in _source.ReadAsync(linkedCts.Token).ConfigureAwait(false))
            {
                ProcessFrame(frame, linkedCts.Token);
            }
        }
        catch (FrameSourceException ex)
        {
            // Raised before this method's task faults, so the UI can render
            // the failure instead of a preview that has quietly stopped
            // updating.
            _logger.LogError(ex, "Scan pipeline: frame source failed ({Source}).", SourceDescription);
            SourceFailed?.Invoke(ex);
            throw;
        }
    }

    /// Stops the loop and disposes the retained frame. Does not dispose the
    /// underlying IFrameSource — the pipeline does not own its lifecycle.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopCts.CancelAsync().ConfigureAwait(false);

        CameraFrame? leftover;
        lock (_retainedLock)
        {
            leftover = _retainedFrame;
            _retainedFrame = null;
            _latestSnapshot = null;
        }

        leftover?.Dispose();

        _stopCts.Dispose();
    }

    /// Detects, retains the frame (disposing whichever one it replaces),
    /// raises FrameProcessed, then evaluates the auto trigger and — on a
    /// fire — captures and raises AutoCaptured.
    private void ProcessFrame(CameraFrame frame, CancellationToken ct)
    {
        var quads = _detector.Detect(frame, MaxDetectionCards);
        var now = MonotonicNow();
        var snapshot = new DetectionSnapshot(quads, _geometry, now);

        CameraFrame? previous;
        lock (_retainedLock)
        {
            previous = _retainedFrame;
            _retainedFrame = frame;
            _latestSnapshot = snapshot;
        }

        // Dispose the frame this one just replaced — never the one we just
        // retained. One owner, one Dispose, across the thread boundary.
        previous?.Dispose();

        // `frame` is still the pipeline's retained frame at this instant, so
        // it is safe to hand out for the duration of this call. Subscribers
        // must not keep the reference past it: the very next frame (or a
        // concurrent CaptureAsync) can take it away immediately after.
        FrameProcessed?.Invoke(frame, snapshot);

        bool fired;
        lock (_triggerLock)
        {
            fired = _trigger.Evaluate(quads, _settings.ExpectedCount, now);
        }

        if (!fired)
        {
            return;
        }

        var cohort = TryCaptureFromRetained(CaptureReason.AutoSettle, ct);

        lock (_triggerLock)
        {
            // Called unconditionally on a fire: the trigger already
            // committed to firing, so the re-arm rule must apply even on the
            // rare race where a concurrent CaptureAsync stole the retained
            // frame first and left nothing here to build a cohort from.
            _trigger.NotifyCaptured();
        }

        if (cohort is { Tiles.Count: > 0 })
        {
            AutoCaptured?.Invoke(cohort);
        }
    }

    /// Takes ownership of the retained frame and its snapshot together under
    /// the lock — leaving the pipeline holding neither until the next frame
    /// arrives — then rectifies and identifies OUTSIDE the lock. Doing the
    /// work inside the lock would block the capture loop; taking a reference
    /// without first clearing it from `_retainedFrame` would let the loop
    /// dispose it out from under this method. Returns null without taking
    /// anything when there is no retained frame, or its snapshot has zero
    /// quads.
    private Cohort? TryCaptureFromRetained(CaptureReason reason, CancellationToken ct)
    {
        CameraFrame ownedFrame;
        DetectionSnapshot ownedSnapshot;

        lock (_retainedLock)
        {
            if (_retainedFrame is null || _latestSnapshot is null || _latestSnapshot.Quads.Count == 0)
            {
                return null;
            }

            ownedFrame = _retainedFrame;
            ownedSnapshot = _latestSnapshot;

            _retainedFrame = null;
            _latestSnapshot = null;
        }

        try
        {
            // Area order (ICardDetector's own ordering, preserved verbatim
            // in ownedSnapshot.Quads and in FrameProcessed's snapshot) is
            // what already chose these survivors out of everything
            // detected. Reading order is applied HERE, after that
            // selection and before any tile exists, so it can only ever
            // reshuffle who already made the cut — never change who did.
            var readingOrderQuads = QuadOrdering.ReadingOrder(ownedSnapshot.Quads);

            var tiles = new List<CohortTile>(readingOrderQuads.Count);
            foreach (var quad in readingOrderQuads)
            {
                ct.ThrowIfCancellationRequested();

                var rectified = _rectifier.Rectify(ownedFrame, quad);
                var candidates = _identifier.Identify(rectified, CandidatesPerTile);
                tiles.Add(new CohortTile(rectified, candidates, _settings.GoodDistance, _settings.OkDistance));
            }

            return new Cohort(Guid.NewGuid(), ownedSnapshot.CapturedAt, _settings.ExpectedCount, reason, tiles);
        }
        finally
        {
            ownedFrame.Dispose();
        }
    }

    /// `startWallClock + stopwatch.Elapsed`, never `DateTimeOffset.UtcNow`
    /// per frame — UtcNow is wall-clock and can step backwards on an NTP
    /// correction, which would stall a 500 ms settle indefinitely (or a
    /// forward jump would satisfy it instantly).
    private DateTimeOffset MonotonicNow() => _startWallClock + _stopwatch.Elapsed;
}
