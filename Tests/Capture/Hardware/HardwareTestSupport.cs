using System.Collections.Concurrent;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LoreFetch.Tests.Capture.Hardware;

/// Shared plumbing for every `[Trait("Category","Hardware")]` test: where
/// saved frames and logs go, and a logger factory that fans every log line
/// out to xunit's `ITestOutputHelper` (visible live in the test runner) and
/// to a log file (so the full run survives after the test window scrolls
/// away).
///
/// The output directory is deliberately never inside the repo — a saved
/// frame is card-camera imagery, and CLAUDE.md's "never commit card
/// imagery" rule applies regardless of who took the photo. `LOREFETCH_HW_OUT`
/// lets a run be redirected (e.g. to keep a specific run's artifacts);
/// absent that, `%TEMP%\lorefetch-hw` is always outside any git worktree.
internal static class HardwareTestSupport
{
    internal static string ResolveOutputDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("LOREFETCH_HW_OUT");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(Path.GetTempPath(), "lorefetch-hw");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// One log file per test, named after it, inside `outputDirectory`.
    /// `Lines` additionally captures every formatted line in-process so a
    /// test can grep its own run for a specific log message (e.g.
    /// `JpegFrameDecoder`'s own periodic decode-time summary) without
    /// re-reading the file it just wrote.
    internal static HardwareLogging CreateLogging(ITestOutputHelper output, string testName, string outputDirectory)
    {
        var logFilePath = Path.Combine(outputDirectory, $"{testName}.log");
        var writer = new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        var lines = new ConcurrentQueue<string>();
        var provider = new TestOutputLoggerProvider(output, writer, lines);

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });

        return new HardwareLogging(factory, writer, logFilePath, lines);
    }

    internal static int GetEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    /// C4-fix. The bound applied to a single harness-side `MoveNextAsync`
    /// call before concluding the product's own watchdog never fired. Must
    /// always exceed the larger of the product's two timeouts —
    /// `ScanSettings.FirstFrameTimeoutMs` (guards the very first frame) and
    /// `FrameWatchdogMs` (guards every frame after) — plus a margin, or the
    /// harness's own bound fires before `FrameWatchdog` ever gets a chance
    /// to raise `FrameSourceException`, and the test observes the harness's
    /// timeout instead of the product's diagnosis.
    ///
    /// `margin` defaults to five seconds ("a margin of several seconds")
    /// for real hardware runs; unit tests exercising this helper against a
    /// synthetic source (see HardwareTestSupportMoveNextBoundTests) pass a
    /// smaller one so the harness-timeout path itself stays fast to test.
    /// `minimumBound` lets a caller widen the bound further for a reason
    /// unrelated to the product's own watchdogs — e.g. UnplugCameraTests
    /// waiting for a human to physically unplug the camera.
    internal static TimeSpan ComputeMoveNextBound(ScanSettings settings, TimeSpan? minimumBound = null, TimeSpan? margin = null)
    {
        var productMs = Math.Max(settings.FirstFrameTimeoutMs, settings.FrameWatchdogMs);
        var bound = TimeSpan.FromMilliseconds(productMs) + (margin ?? TimeSpan.FromSeconds(5));
        return minimumBound is { } min && min > bound ? min : bound;
    }

    /// C4-fix. A single `MoveNextAsync`, bounded by `ComputeMoveNextBound`,
    /// that never disposes an enumerator while its `MoveNextAsync` is still
    /// pending — the same rule `FrameWatchdog.Watch` already follows in
    /// production code (src/LoreFetch.Capture/FrameWatchdog.cs).
    ///
    /// Before this fix, every hardware test called
    /// `enumerator.MoveNextAsync().AsTask().WaitAsync(&lt;constant&gt;, ct)`
    /// directly, with a constant (5 s in
    /// `Negotiates1080pMjpgAndDeliversLiveFrames`) shorter than
    /// `ScanSettings.FirstFrameTimeoutMs`'s 10 s default. When
    /// `device.StartAsync` was chaos-deleted, the harness's own 5 s bound
    /// fired first — before `FrameWatchdog` ever raised its
    /// `FrameSourceException` — and the test's `finally { await
    /// enumerator.DisposeAsync(); }` then disposed the compiler-generated
    /// async-iterator enumerator while its `MoveNextAsync` was still
    /// pending, which throws `NotSupportedException` and masks the real
    /// failure entirely.
    ///
    /// - If the source completes or throws — including the product's own
    ///   `FrameSourceException` — before the bound elapses, that outcome
    ///   propagates unchanged: the task has already settled by the time it
    ///   is observed, so it is always safe for the caller's own
    ///   `finally { await enumerator.DisposeAsync(); }` to run next.
    /// - If the bound itself elapses first, this cancels
    ///   `enumerationCts` — giving the still-pending `MoveNextAsync`
    ///   something to react to — then awaits that same task, bounded by
    ///   `settleTimeout`, swallowing whatever it settles with (its own
    ///   `OperationCanceledException`, or the product's exception if it
    ///   raced the cancellation). Only once that wait returns does this
    ///   throw `HarnessTimeoutException` — never `TimeoutException`, never
    ///   `NotSupportedException` — and by then the pending task is
    ///   guaranteed to have settled, so disposal afterward is safe too.
    internal static async Task<bool> MoveNextWithHarnessBoundAsync<T>(
        IAsyncEnumerator<T> enumerator,
        ScanSettings settings,
        CancellationTokenSource enumerationCts,
        CancellationToken testCancellationToken,
        TimeSpan? minimumBound = null,
        TimeSpan? margin = null,
        TimeSpan? settleTimeout = null)
    {
        var bound = ComputeMoveNextBound(settings, minimumBound, margin);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        try
        {
            return await moveNextTask.WaitAsync(bound, testCancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Cancel FIRST — never dispose while MoveNextAsync is pending.
            enumerationCts.Cancel();
            try
            {
                await moveNextTask.WaitAsync(settleTimeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
                // Expected: our own cancellation surfacing as
                // OperationCanceledException, or the product's own
                // exception racing the cancel. Either is fine here — this
                // catch exists only to guarantee the task has settled
                // before we return control to the caller; the failure that
                // actually matters is reported below.
            }

            throw new HarnessTimeoutException(
                $"The product's watchdog did not fire within the harness bound of {bound.TotalSeconds:F0}s " +
                $"(derived from FirstFrameTimeoutMs={settings.FirstFrameTimeoutMs}, FrameWatchdogMs={settings.FrameWatchdogMs}, plus margin" +
                (minimumBound is { } min ? $", minimumBound={min.TotalSeconds:F0}s" : string.Empty) +
                "). This is the harness's own bound expiring — not a FrameSourceException from the product.");
        }
    }
}

/// C4-fix. Thrown by `HardwareTestSupport.MoveNextWithHarnessBoundAsync`
/// when the harness's own bound expires before the product either yields a
/// frame or raises its own `Core.Abstractions.FrameSourceException`.
/// Distinguishing the two is the whole point of the fix: before it, this
/// case surfaced as an unrelated `NotSupportedException` from disposing an
/// enumerator with a pending `MoveNextAsync`.
internal sealed class HarnessTimeoutException : Exception
{
    internal HarnessTimeoutException(string message)
        : base(message)
    {
    }
}

/// Bundles an `ILoggerFactory` with the file writer backing it and the
/// captured line buffer, so a test disposes exactly one thing and still has
/// `LogFilePath`/`Lines` available afterward for its own report.
internal sealed class HardwareLogging : IDisposable
{
    private readonly TextWriter _writer;

    internal HardwareLogging(ILoggerFactory factory, TextWriter writer, string logFilePath, ConcurrentQueue<string> lines)
    {
        Factory = factory;
        _writer = writer;
        LogFilePath = logFilePath;
        Lines = lines;
    }

    internal ILoggerFactory Factory { get; }

    internal string LogFilePath { get; }

    internal ConcurrentQueue<string> Lines { get; }

    public void Dispose()
    {
        Factory.Dispose();
        _writer.Dispose();
    }
}

/// `ILoggerProvider` adapter: every log line goes to xunit's
/// `ITestOutputHelper`, to a file, and into an in-memory queue the test
/// itself can inspect. A single lock serializes all three sinks because the
/// capture callback runs on FlashCap's own thread while the test's async
/// loop runs on another — without it, interleaved writes to the same
/// `StreamWriter` could corrupt a line.
internal sealed class TestOutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;
    private readonly TextWriter _writer;
    private readonly ConcurrentQueue<string> _lines;
    private readonly object _gate = new();

    internal TestOutputLoggerProvider(ITestOutputHelper output, TextWriter writer, ConcurrentQueue<string> lines)
    {
        _output = output;
        _writer = writer;
        _lines = lines;
    }

    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);

            try
            {
                // ITestOutputHelper throws InvalidOperationException once
                // xunit has torn down the test's output buffer (e.g. a log
                // line arriving from a background capture thread after the
                // test method itself has already returned, such as during
                // DisposeAsync). That race is expected here — the file and
                // the in-memory queue still get the line — so it must never
                // surface as an unrelated test failure.
                _output.WriteLine(line);
            }
            catch (InvalidOperationException)
            {
            }

            _writer.WriteLine(line);
        }
    }

    private sealed class TestOutputLogger : ILogger
    {
        private readonly string _category;
        private readonly TestOutputLoggerProvider _provider;

        internal TestOutputLogger(string category, TestOutputLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{logLevel}] {_category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}
