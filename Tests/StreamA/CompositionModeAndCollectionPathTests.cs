using LoreFetch.App;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Unit tests for package I1's two env-var resolution functions —
/// <see cref="AppComposition.ResolveCompositionMode"/> and
/// <see cref="AppComposition.ResolveCollectionPath"/>. Both take the raw env
/// var value as a parameter rather than reading
/// <see cref="Environment.GetEnvironmentVariable"/> themselves (the same
/// shape as the existing <see cref="AppComposition.ChooseFrameFolder"/>), so
/// every case here is a pure function call — no process-global env var is
/// ever mutated, and <see cref="ResolveCollectionPath_NoEnvVar_WouldUseMyDocuments_ButThisSuiteNeverExercisesThat"/>
/// documents (without running) the one branch this suite deliberately never
/// exercises, per the work package's "tests must never touch the real
/// Documents folder" rule.
public class CompositionModeAndCollectionPathTests : IDisposable
{
    private readonly ILogger _nullLogger = NullLoggerFactory.Instance.CreateLogger("test");
    private readonly List<string> _toDelete = [];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fakes")]
    [InlineData("FAKES")]
    [InlineData("Fakes")]
    public void ResolveCompositionMode_UnsetOrFakes_ReturnsFakes(string? envVar)
    {
        Assert.Equal(CompositionMode.Fakes, AppComposition.ResolveCompositionMode(envVar, _nullLogger));
    }

    [Theory]
    [InlineData("real")]
    [InlineData("REAL")]
    [InlineData("Real")]
    public void ResolveCompositionMode_Real_ReturnsReal(string envVar)
    {
        Assert.Equal(CompositionMode.Real, AppComposition.ResolveCompositionMode(envVar, _nullLogger));
    }

    /// The default-stays-Fakes rule from the work package's override: this
    /// package does not flip the default (I3 does, once the camera is
    /// wired), so unset must land on Fakes, not Real.
    [Fact]
    public void ResolveCompositionMode_Unset_DefaultsToFakes_NotReal()
    {
        Assert.Equal(CompositionMode.Fakes, AppComposition.ResolveCompositionMode(null, _nullLogger));
    }

    /// The "never crash" half of the override: a typo must log, not throw,
    /// and must still leave the app startable in Fakes mode.
    [Fact]
    public void ResolveCompositionMode_UnknownValue_LogsAnErrorAndFallsBackToFakes()
    {
        var logger = new CapturingLogger();

        var mode = AppComposition.ResolveCompositionMode("bogus", logger);

        Assert.Equal(CompositionMode.Fakes, mode);
        Assert.Contains(logger.Messages, m => m.Contains("bogus", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
    }

    /// Chaos-tested: swapping the two `string.Equals` branches so "real"
    /// wins ties and falls through to Fakes only for "fakes" itself would
    /// make this test's "REAL" case land on Fakes instead — a case
    /// mismatch would silently start the app in the wrong mode.
    [Fact]
    public void ResolveCompositionMode_IsCaseInsensitive_ForBothRecognisedValues()
    {
        Assert.Equal(CompositionMode.Fakes, AppComposition.ResolveCompositionMode("fAkEs", _nullLogger));
        Assert.Equal(CompositionMode.Real, AppComposition.ResolveCompositionMode("rEaL", _nullLogger));
    }

    [Fact]
    public void ResolveCollectionPath_EnvVarSet_UsesThatPath_AndCreatesTheDirectory()
    {
        var dir = MakeTempDirPath();
        var expectedPath = Path.Combine(dir, "collection.csv");
        Assert.False(Directory.Exists(dir), "Precondition: the directory must not exist yet.");

        var resolved = AppComposition.ResolveCollectionPath(expectedPath, _nullLogger);

        Assert.Equal(Path.GetFullPath(expectedPath), resolved);
        Assert.True(Directory.Exists(dir), "ResolveCollectionPath must create the directory when missing.");
    }

    [Fact]
    public void ResolveCollectionPath_EnvVarSet_DirectoryAlreadyExists_DoesNotThrow()
    {
        var dir = MakeTempDirPath();
        Directory.CreateDirectory(dir);
        var expectedPath = Path.Combine(dir, "collection.csv");

        var resolved = AppComposition.ResolveCollectionPath(expectedPath, _nullLogger);

        Assert.Equal(Path.GetFullPath(expectedPath), resolved);
        Assert.True(Directory.Exists(dir));
    }

    // Deliberately NOT tested here: AppComposition.ResolveCollectionPath(null/empty/
    // whitespace, ...) falls back to "<MyDocuments>/LoreFetch/collection.csv" and
    // creates that directory if missing — calling it with a falsy envVar in a unit
    // test would create a real "LoreFetch" folder under the developer's own
    // Documents folder, which is exactly what the work package's "tests must never
    // touch the real Documents folder — always a temp path" rule forbids. That
    // fallback branch is proven instead by the LOREFETCH_MODE=real smoke launch
    // (which never sets LOREFETCH_COLLECTION) and by code review of the two-line
    // ternary itself.

    public void Dispose()
    {
        foreach (var dir in _toDelete)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string MakeTempDirPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-streamA-{Guid.NewGuid():N}");
        _toDelete.Add(dir);
        return dir;
    }

    /// Captures every formatted message AND its `LogLevel`, so a test can
    /// assert both "what was logged" and "at what severity" without wiring
    /// a real sink. `Microsoft.Extensions.Logging` gives no first-party
    /// in-memory capture type (same rationale as `Tests/StreamD`'s own
    /// `CapturingLogger`, which lives in a sibling test assembly this
    /// project cannot reference).
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }
    }
}
