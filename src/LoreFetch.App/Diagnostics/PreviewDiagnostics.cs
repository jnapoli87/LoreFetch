namespace LoreFetch.App.Diagnostics;

/// <summary>
/// One snapshot from <see cref="PreviewDiagnostics.Sample"/>: the two rates
/// A10 must record. <see cref="PreviewFramesPerSecond"/> counts frames
/// ACTUALLY BLITTED to the live <c>WriteableBitmap</c> (MainWindow's
/// <c>OnRenderFrame</c>) — never frames merely received from the pipeline,
/// which is throttled down to ~15 fps and can be much lower than the
/// pipeline's own rate. <see cref="PipelineFramesPerSecond"/> counts frames
/// <c>IScanPipeline.FrameProcessed</c> raised, independent of whether any of
/// them were actually rendered.
/// </summary>
public readonly record struct PreviewDiagnosticsSample(
    double PreviewFramesPerSecond,
    double PipelineFramesPerSecond);

/// <summary>
/// Holds the two independent <see cref="RateCounter"/>s A10 needs — one for
/// frames the pipeline processed, one for frames the preview actually
/// rendered — and reads both together so a reporter never samples one a
/// moment apart from the other.
/// </summary>
/// <remarks>
/// The two counters are DELIBERATELY separate instances, not one counter
/// incremented from two call sites: collapsing them would make "rendered
/// fps" silently read the same as "received fps", which is exactly the
/// preview-choppiness class of bug DECISIONS.md's "Avalonia preview" note
/// warns about (a preview that never actually re-renders can still report
/// a healthy frame rate if the metric only tracks arrivals). Call
/// <see cref="RecordFrameProcessed"/> from <c>OnFrameProcessed</c> (every
/// frame the pipeline hands the UI) and <see cref="RecordFrameRendered"/>
/// only from inside <c>OnRenderFrame</c>, after the bitmap lock is disposed
/// and the blit has actually happened.
/// </remarks>
public sealed class PreviewDiagnostics
{
    private readonly RateCounter _processed;
    private readonly RateCounter _rendered;

    public PreviewDiagnostics(DateTimeOffset now)
    {
        _processed = new RateCounter(now);
        _rendered = new RateCounter(now);
    }

    /// <summary>Call once per <c>IScanPipeline.FrameProcessed</c> callback.</summary>
    public void RecordFrameProcessed() => _processed.Increment();

    /// <summary>
    /// Call once per frame actually blitted to the live <c>WriteableBitmap</c>
    /// — i.e. from inside <c>OnRenderFrame</c>, never from
    /// <c>OnFrameProcessed</c>.
    /// </summary>
    public void RecordFrameRendered() => _rendered.Increment();

    /// <summary>
    /// Reads and resets both counters' windows together, at the same
    /// <paramref name="now"/>, so the two rates in the returned sample cover
    /// the same span of wall-clock time.
    /// </summary>
    public PreviewDiagnosticsSample Sample(DateTimeOffset now) =>
        new(
            PreviewFramesPerSecond: _rendered.TakeRateAndReset(now),
            PipelineFramesPerSecond: _processed.TakeRateAndReset(now));
}
