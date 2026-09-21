using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Scanning;

public sealed record DetectionSnapshot(
    IReadOnlyList<CardQuad> Quads,
    FrameGeometry Geometry,
    DateTimeOffset CapturedAt);

/// The one component that composes source → detector → rectifier →
/// identifier → trigger into cohorts.
public interface IScanPipeline : IAsyncDisposable
{
    /// Raised on the pipeline's background thread for every frame it processes.
    /// `frame` is valid ONLY for the duration of the callback — copy what you
    /// need (the preview converts BGR→BGRA into its own bitmap anyway) and
    /// never retain the reference. A slow handler costs latency, not memory.
    event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;

    /// Raised when the auto trigger fires and produced a non-empty cohort.
    /// Raised on the pipeline's background thread, like FrameProcessed — the
    /// UI must marshal to its own thread before touching any control.
    event Action<Cohort>? AutoCaptured;

    /// The frame source failed — device unplugged, taken by another app, or
    /// silent past the watchdog. Raised on the pipeline's background thread
    /// immediately before RunAsync faults, so the UI can render the failure
    /// instead of showing a preview that has quietly stopped updating.
    event Action<FrameSourceException>? SourceFailed;

    /// The frame source's Description, captured once at construction, so the
    /// status line never needs to touch an IFrameSource.
    string SourceDescription { get; }

    /// Rectifies and identifies the frame the LATEST snapshot came from —
    /// never a newer frame against older quads. Ignores ExpectedCount.
    /// Returns null when the latest snapshot has zero quads.
    ///
    /// Safe to call from ANY thread, concurrently with the pipeline's own
    /// loop: it takes ownership of the retained frame under a lock, then does
    /// the rectify-and-identify work outside it. Async because up to nine
    /// identify passes is far too much to run inline on the UI thread — the
    /// UI awaits this from its key handler and stays responsive.
    Task<Cohort?> CaptureAsync(CancellationToken ct);

    /// Consumes the frame source until cancelled.
    Task RunAsync(CancellationToken ct);
}
