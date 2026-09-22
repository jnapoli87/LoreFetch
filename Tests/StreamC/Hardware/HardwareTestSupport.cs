using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LoreFetch.Tests.StreamC.Hardware;

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
