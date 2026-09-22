using LoreFetch.App.Diagnostics;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// A10-prep item 5: unit tests for the pure part of the diagnostics —
/// <see cref="RateCounter"/>'s windowing and reset behaviour, and
/// <see cref="PreviewDiagnostics"/>'s independence of its two counters.
/// </summary>
public class RateCounterTests
{
    [Fact]
    public void TakeRateAndReset_ComputesCountOverElapsedWindow()
    {
        var t0 = DateTimeOffset.UtcNow;
        var counter = new RateCounter(t0);

        counter.Increment();
        counter.Increment();
        counter.Increment();
        counter.Increment(); // 4 occurrences

        var rate = counter.TakeRateAndReset(t0.AddSeconds(2)); // 4 / 2s = 2.0/s

        Assert.Equal(2.0, rate, precision: 6);
    }

    [Fact]
    public void TakeRateAndReset_ResetsCountAndWindowStart_SoTheNextCallIsIndependent()
    {
        var t0 = DateTimeOffset.UtcNow;
        var counter = new RateCounter(t0);

        counter.Increment();
        counter.Increment(); // 2 in the first window

        var t1 = t0.AddSeconds(2);
        var rate1 = counter.TakeRateAndReset(t1);
        Assert.Equal(1.0, rate1, precision: 6); // 2 / 2s

        // No further Increment calls — the second window must report 0,
        // not a rate that still includes the first window's count (a
        // counter that never resets would instead report 2 / (elapsed
        // since t0) here, which is nonzero).
        var t2 = t1.AddSeconds(2);
        var rate2 = counter.TakeRateAndReset(t2);
        Assert.Equal(0.0, rate2, precision: 6);
    }

    [Fact]
    public void TakeRateAndReset_MultipleWindows_EachMeasuresOnlyItsOwnIncrements()
    {
        var t0 = DateTimeOffset.UtcNow;
        var counter = new RateCounter(t0);

        counter.Increment();
        var rate1 = counter.TakeRateAndReset(t0.AddSeconds(1)); // 1/1s
        Assert.Equal(1.0, rate1, precision: 6);

        counter.Increment();
        counter.Increment();
        counter.Increment();
        var rate2 = counter.TakeRateAndReset(t0.AddSeconds(1).AddSeconds(3)); // 3/3s
        Assert.Equal(1.0, rate2, precision: 6);
    }

    [Fact]
    public void TakeRateAndReset_ZeroElapsed_ReturnsZeroRatherThanInfinity()
    {
        var t0 = DateTimeOffset.UtcNow;
        var counter = new RateCounter(t0);

        counter.Increment();

        var rate = counter.TakeRateAndReset(t0); // same instant — elapsed = 0

        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void PreviewDiagnostics_TracksRenderedAndProcessedIndependently()
    {
        var t0 = DateTimeOffset.UtcNow;
        var diagnostics = new PreviewDiagnostics(t0);

        for (var i = 0; i < 100; i++)
        {
            diagnostics.RecordFrameProcessed();
        }

        for (var i = 0; i < 3; i++)
        {
            diagnostics.RecordFrameRendered();
        }

        var sample = diagnostics.Sample(t0.AddSeconds(10));

        // 100 processed / 10s = 10/s; 3 rendered / 10s = 0.3/s — deliberately
        // very different, so a bug that collapses the two counters (e.g.
        // "rendered" secretly counting every received frame too) is caught
        // by PipelineFramesPerSecond no longer being 10.0, or
        // PreviewFramesPerSecond no longer being 0.3.
        Assert.Equal(10.0, sample.PipelineFramesPerSecond, precision: 6);
        Assert.Equal(0.3, sample.PreviewFramesPerSecond, precision: 6);
    }

    [Fact]
    public void PreviewDiagnostics_NoRenderedFrames_ReportsZeroPreviewFps_EvenWithManyProcessed()
    {
        // Directly exercises the "counts frames received instead of frames
        // rendered" failure mode: many frames PROCESSED, zero RENDERED
        // (e.g. the window never showed, or the ~15 fps gate dropped every
        // one) must report exactly 0 preview fps, not some fraction of the
        // processed rate.
        var t0 = DateTimeOffset.UtcNow;
        var diagnostics = new PreviewDiagnostics(t0);

        for (var i = 0; i < 500; i++)
        {
            diagnostics.RecordFrameProcessed();
        }

        var sample = diagnostics.Sample(t0.AddSeconds(5));

        Assert.Equal(0.0, sample.PreviewFramesPerSecond);
        Assert.Equal(100.0, sample.PipelineFramesPerSecond, precision: 6);
    }
}
