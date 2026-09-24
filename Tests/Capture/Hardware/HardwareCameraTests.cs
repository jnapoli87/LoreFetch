using System.Diagnostics;
using System.Text.RegularExpressions;
using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Capture.Hardware;

/// C4-prep: the hardware test harness the orchestrator runs on the real
/// C920 (docs/design/capture.md's "Done when" items that need a camera:
/// negotiated 1080p30 MJPG, flat memory over a sustained run, a slow
/// consumer producing latency rather than growth). Every test here opens
/// the real device through the PUBLIC `WebcamFrameSourceFactory` — nothing
/// internal is reached into — because that is exactly the seam a real
/// caller uses.
///
/// Deliberately NOT run by this stream's own verification: opening a
/// physical camera from an unattended CI-style run is exactly what
/// `Category=Hardware` exists to keep out of both CI legs (docs/TESTING.md
/// §CI) and out of this implementer's own "run the suite" step. The
/// orchestrator runs these by name, with the user present at the machine.
[Trait("Category", "Hardware")]
public sealed class HardwareCameraTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _outputDirectory;
    private readonly HardwareLogging _logging;

    public HardwareCameraTests(ITestOutputHelper output)
    {
        _output = output;
        _outputDirectory = HardwareTestSupport.ResolveOutputDirectory();
        output.WriteLine($"Hardware test output directory: {_outputDirectory}");
        _logging = HardwareTestSupport.CreateLogging(output, GetType().Name, _outputDirectory);
    }

    public void Dispose() => _logging.Dispose();

    /// Proves C1/C2's whole point end to end: the negotiated format really
    /// is 1920x1080 MJPG at >= 30 fps (read from the device's own
    /// characteristics via `Description`, not assumed), `Geometry` reflects
    /// the default 90 degree rotation applied in the source (1080 wide x
    /// 1920 high — read from `ScanSettings`'s own default, never
    /// hardcoded), and frames actually keep arriving for 10 seconds.
    [Fact]
    public async Task Negotiates1080pMjpgAndDeliversLiveFrames()
    {
        var settings = new ScanSettings();
        var factory = new WebcamFrameSourceFactory(_logging.Factory);

        await using var source = await factory.CreateAsync(settings, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _output.WriteLine($"Negotiated: {source.Description}");
        Assert.Contains("1920x1080", source.Description);
        Assert.Contains("MJPG", source.Description);

        var fps = ParseFps(source.Description);
        _output.WriteLine($"Parsed fps from Description: {fps}");
        Assert.True(fps >= 30.0, $"Expected negotiated fps >= 30 from Description '{source.Description}', got {fps}.");

        // Read the expected geometry from ScanSettings's own default rather
        // than hardcoding 1080x1920 — CameraRotationDegrees defaults to 90,
        // and 90/270 swap the axes (JpegFrameDecoder.ComputeGeometry).
        var rotation = settings.CameraRotationDegrees;
        var (expectedWidth, expectedHeight) = rotation is 90 or 270 ? (1080, 1920) : (1920, 1080);
        Assert.Equal(rotation, source.Geometry.RotationDegrees);
        Assert.Equal(expectedWidth, source.Geometry.Width);
        Assert.Equal(expectedHeight, source.Geometry.Height);

        var moveNextMs = new List<double>();
        CameraFrame? last = null;
        var frameCount = 0;

        // The absolute bound must itself exceed the per-MoveNext harness
        // bound below (settings-derived, ~15s by default) plus the 10s read
        // window, or this outer token could cut the run short before the
        // per-call bound even gets a chance to matter.
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var enumerator = source.ReadAsync(readCts.Token).GetAsyncEnumerator(readCts.Token);
        var runSw = Stopwatch.StartNew();
        try
        {
            while (runSw.Elapsed < TimeSpan.FromSeconds(10))
            {
                var moveNextSw = Stopwatch.StartNew();
                // C4-fix: was `.WaitAsync(TimeSpan.FromSeconds(5), ...)` — a
                // constant shorter than ScanSettings.FirstFrameTimeoutMs's
                // 10s default, which let the harness's own timeout fire
                // before FrameWatchdog's FrameSourceException ever could,
                // and then masked it with NotSupportedException at dispose.
                // See HardwareTestSupport.MoveNextWithHarnessBoundAsync.
                var hasNext = await HardwareTestSupport.MoveNextWithHarnessBoundAsync(
                    enumerator, settings, readCts, TestContext.Current.CancellationToken);
                moveNextSw.Stop();

                if (!hasNext)
                {
                    break;
                }

                moveNextMs.Add(moveNextSw.Elapsed.TotalMilliseconds);
                last?.Dispose();
                last = enumerator.Current;
                frameCount++;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        runSw.Stop();

        var deliveredFps = frameCount / runSw.Elapsed.TotalSeconds;
        _output.WriteLine($"Delivered {frameCount} frames in {runSw.Elapsed.TotalSeconds:F1}s = {deliveredFps:F1} fps.");
        Assert.True(
            deliveredFps >= 20.0,
            $"Expected delivered fps >= 20 (loose — a slow-light C920 may drop exposure-limited fps), measured {deliveredFps:F1}.");

        ReportPercentiles("MoveNextAsync elapsed", moveNextMs);

        // JpegFrameDecoder rolls its own per-frame decode times up into an
        // Information-level summary every DecodeSummaryIntervalFrames (150)
        // frames (docs/design/capture.md C6) — surface the most recent one
        // this run actually logged, if any 150-frame window completed.
        var decodeSummaryLine = _logging.Lines.LastOrDefault(l => l.Contains("Capture: decoded", StringComparison.Ordinal));
        _output.WriteLine(decodeSummaryLine is not null
            ? $"Decoder's own summary: {decodeSummaryLine}"
            : "Decoder's own summary: none logged (fewer than 150 frames were decoded this run).");

        Assert.NotNull(last);
        var pngPath = Path.Combine(_outputDirectory, "last-frame.png");
        using (var mat = Mat.FromPixelData(last!.Height, last.Width, MatType.CV_8UC3, last.Pixels.ToArray(), last.Stride))
        {
            Cv2.ImWrite(pngPath, mat);
        }

        last.Dispose();
        _output.WriteLine($"Saved last frame to {pngPath}");
    }

    /// Proof that pooling and disposal are correct over a real, extended
    /// run: private bytes must stay flat rather than climbing at the
    /// ~186 MB/s an unpooled frame stream would produce (CLAUDE.md's C920
    /// trap table). Duration is configurable because 3 minutes is a
    /// reasonable default but the orchestrator may want longer.
    [Fact]
    public async Task SustainedRunKeepsMemoryFlat()
    {
        var minutes = HardwareTestSupport.GetEnvInt("LOREFETCH_HW_MINUTES", 3);
        var duration = TimeSpan.FromMinutes(minutes);
        _output.WriteLine($"Sustained run duration: {minutes} minute(s).");

        var settings = new ScanSettings();
        var factory = new WebcamFrameSourceFactory(_logging.Factory);
        await using var source = await factory.CreateAsync(settings, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(60));
        var samples = new List<MemorySample>();
        var frameCount = 0L;
        var overallSw = Stopwatch.StartNew();
        var nextSampleAt = TimeSpan.FromSeconds(10);
        const int warmupSeconds = 30;
        long? postWarmupBaseline = null;
        var maxPrivateAfterWarmup = long.MinValue;

        var enumerator = source.ReadAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            while (overallSw.Elapsed < duration)
            {
                // C4-fix: see Negotiates1080pMjpgAndDeliversLiveFrames — a
                // raw constant bound here shorter than the product's own
                // watchdogs risks the same NotSupportedException-masks-
                // FrameSourceException failure mode.
                if (!await HardwareTestSupport.MoveNextWithHarnessBoundAsync(enumerator, settings, cts, TestContext.Current.CancellationToken))
                {
                    break;
                }

                enumerator.Current.Dispose(); // consume promptly — the point of this test is the source's own behaviour, not a backlog we create
                frameCount++;

                if (overallSw.Elapsed < nextSampleAt)
                {
                    continue;
                }

                var sample = TakeSample(overallSw.Elapsed, frameCount);
                samples.Add(sample);
                _output.WriteLine(sample.ToString());

                if (overallSw.Elapsed >= TimeSpan.FromSeconds(warmupSeconds))
                {
                    postWarmupBaseline ??= sample.PrivateBytes;
                    maxPrivateAfterWarmup = Math.Max(maxPrivateAfterWarmup, sample.PrivateBytes);
                }

                nextSampleAt += TimeSpan.FromSeconds(10);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        _output.WriteLine($"Total frames: {frameCount} over {overallSw.Elapsed.TotalSeconds:F0}s.");
        Assert.True(postWarmupBaseline.HasValue, $"Run was shorter than the {warmupSeconds}s warm-up window — no post-warm-up samples were taken.");

        var growthBytes = maxPrivateAfterWarmup - postWarmupBaseline!.Value;
        _output.WriteLine($"Post-warm-up private-bytes growth: {growthBytes / 1_000_000.0:F1} MB (baseline {postWarmupBaseline.Value / 1_000_000.0:F1} MB, max {maxPrivateAfterWarmup / 1_000_000.0:F1} MB).");

        var slopeBytesPerSec = ComputeSlope(samples.Where(s => s.Elapsed >= TimeSpan.FromSeconds(warmupSeconds)).ToList());
        _output.WriteLine($"Post-warm-up private-bytes slope: {slopeBytesPerSec:F1} bytes/s ({slopeBytesPerSec * 60 / 1_000_000.0:F2} MB/min).");

        Assert.True(
            growthBytes < 100_000_000,
            $"Expected post-warm-up private-bytes growth < 100 MB (an unpooled 6.2 MB/frame @30fps leak would blow through this in seconds); measured {growthBytes / 1_000_000.0:F1} MB.");
    }

    /// Proof that `DropOldest` genuinely drops rather than queues: a
    /// consumer that awaits 500 ms per frame must see growing *latency*
    /// (roughly one consumer period plus decode), never growing memory —
    /// the failure mode this test exists to catch is a channel that queues
    /// instead of evicting, which looks fine until exactly this load hits it.
    [Fact]
    public async Task SlowConsumerSeesLatencyNotGrowth()
    {
        var duration = TimeSpan.FromSeconds(60);
        var settings = new ScanSettings();
        var factory = new WebcamFrameSourceFactory(_logging.Factory);
        await using var source = await factory.CreateAsync(settings, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(30));
        var latenciesMs = new List<double>();
        long? baselinePrivateBytes = null;
        var maxPrivateBytes = long.MinValue;
        var overallSw = Stopwatch.StartNew();

        var enumerator = source.ReadAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            while (overallSw.Elapsed < duration)
            {
                // C4-fix: see Negotiates1080pMjpgAndDeliversLiveFrames.
                if (!await HardwareTestSupport.MoveNextWithHarnessBoundAsync(enumerator, settings, cts, TestContext.Current.CancellationToken))
                {
                    break;
                }

                using var frame = enumerator.Current;
                var latency = DateTimeOffset.UtcNow - frame.CapturedAt;
                latenciesMs.Add(latency.TotalMilliseconds);

                if (overallSw.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    var privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
                    baselinePrivateBytes ??= privateBytes;
                    maxPrivateBytes = Math.Max(maxPrivateBytes, privateBytes);
                }

                // The slow consumer this test is named for.
                await Task.Delay(500, cts.Token);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        _output.WriteLine($"Consumed {latenciesMs.Count} frames over {overallSw.Elapsed.TotalSeconds:F0}s at one frame per ~500ms consumer period.");
        ReportPercentiles("Frame latency (CapturedAt -> consumed)", latenciesMs);

        Assert.True(baselinePrivateBytes.HasValue, "Run was shorter than the 10s baseline window.");
        var growthBytes = maxPrivateBytes - baselinePrivateBytes!.Value;
        _output.WriteLine($"Private-bytes growth vs 10s baseline: {growthBytes / 1_000_000.0:F1} MB.");
        Assert.True(growthBytes < 100_000_000, $"Expected private-bytes growth < 100 MB under a slow consumer; measured {growthBytes / 1_000_000.0:F1} MB.");

        var lastTwenty = latenciesMs.TakeLast(20).ToList();
        Assert.True(lastTwenty.Count > 0, "Expected at least one frame in the tail window.");
        var maxLastTwenty = lastTwenty.Max();
        _output.WriteLine($"Max latency of last {lastTwenty.Count} frames: {maxLastTwenty:F0} ms.");
        Assert.True(
            maxLastTwenty < 1500,
            $"Expected the last 20 frames' latency to stay bounded (newest-frame-only means roughly one consumer period plus decode, not an upward trend); measured max {maxLastTwenty:F0} ms.");
    }

    /// Proves `DisposeAsync` genuinely releases the FlashCap device handle —
    /// the C2-fix commit's cleanup path exists precisely so a failed or
    /// finished open never leaves the next one unable to open the same
    /// camera. Also the test to re-run after re-plugging the camera
    /// following the unplug test.
    [Fact]
    public async Task ReopenAfterDisposeWorks()
    {
        var settings = new ScanSettings();
        var factory = new WebcamFrameSourceFactory(_logging.Factory);

        _output.WriteLine("First open.");
        await FirstOpenReadDispose();

        _output.WriteLine("Second open — this is the one that fails if the first DisposeAsync leaked the device handle.");
        await FirstOpenReadDispose();

        async Task FirstOpenReadDispose()
        {
            var source = await factory.CreateAsync(settings, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var frameCount = 0;
                var sw = Stopwatch.StartNew();
                await foreach (var frame in source.ReadAsync(cts.Token))
                {
                    frame.Dispose();
                    frameCount++;
                    if (sw.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        break;
                    }
                }

                _output.WriteLine($"Read {frameCount} frames in {sw.Elapsed.TotalSeconds:F1}s.");
                Assert.True(frameCount > 0, "Expected at least one frame during the 2s read window.");
            }
            finally
            {
                await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }
    }

    private MemorySample TakeSample(TimeSpan elapsed, long frameCount)
    {
        var gcBytes = GC.GetTotalMemory(false);
        var heapBytes = (long)GC.GetGCMemoryInfo().HeapSizeBytes;
        var privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        return new MemorySample(elapsed, gcBytes, heapBytes, privateBytes, frameCount);
    }

    private static double ComputeSlope(IReadOnlyList<MemorySample> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var xs = samples.Select(s => s.Elapsed.TotalSeconds).ToArray();
        var ys = samples.Select(s => (double)s.PrivateBytes).ToArray();
        var n = xs.Length;
        var meanX = xs.Average();
        var meanY = ys.Average();

        var numerator = 0.0;
        var denominator = 0.0;
        for (var i = 0; i < n; i++)
        {
            numerator += (xs[i] - meanX) * (ys[i] - meanY);
            denominator += (xs[i] - meanX) * (xs[i] - meanX);
        }

        return denominator == 0 ? 0 : numerator / denominator;
    }

    private void ReportPercentiles(string label, IReadOnlyList<double> valuesMs)
    {
        if (valuesMs.Count == 0)
        {
            _output.WriteLine($"{label}: no samples.");
            return;
        }

        var sorted = valuesMs.OrderBy(v => v).ToList();
        var mean = sorted.Average();
        var p95Index = (int)Math.Clamp(Math.Ceiling(0.95 * sorted.Count) - 1, 0, sorted.Count - 1);
        var p95 = sorted[p95Index];
        var max = sorted[^1];

        _output.WriteLine($"{label}: n={sorted.Count} mean={mean:F2}ms p95={p95:F2}ms max={max:F2}ms");
    }

    private static double ParseFps(string description)
    {
        var match = Regex.Match(description, @"@([\d.]+)fps", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, $"Could not parse fps out of Description '{description}'.");
        return double.Parse(match.Groups[1].Value);
    }

    private readonly record struct MemorySample(TimeSpan Elapsed, long GcBytes, long HeapBytes, long PrivateBytes, long FrameCount)
    {
        public override string ToString() =>
            $"t={Elapsed.TotalSeconds:F0}s gc={GcBytes / 1_000_000.0:F1}MB heap={HeapBytes / 1_000_000.0:F1}MB private={PrivateBytes / 1_000_000.0:F1}MB frames={FrameCount}";
    }
}
