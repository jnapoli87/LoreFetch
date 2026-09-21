using LoreFetch.Core.Abstractions;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// A test-local `IAutoCaptureTrigger` that fires on exactly the Nth call to
/// `Evaluate` (orchestration finding V9) — the end-to-end suite's stand-in
/// for stream A's real settle-timer trigger, which does not exist yet, so
/// `ScanPipeline.AutoCaptured` can still be exercised through the real
/// composition path. Deliberately simpler than a timing-based trigger:
/// firing on a known call count is deterministic, where firing on elapsed
/// wall-clock time would make this suite flaky under CI scheduling jitter.
/// Passing `fireOnCall: int.MaxValue` gives a trigger that (for any run
/// this short) never fires — the manual-capture cases' stand-in for "no
/// auto-capture at all".
public sealed class ScriptedTrigger : IAutoCaptureTrigger
{
    private readonly int _fireOnCall;
    private int _callCount;

    public ScriptedTrigger(int fireOnCall)
    {
        if (fireOnCall < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fireOnCall), fireOnCall, "fireOnCall must be >= 1.");
        }

        _fireOnCall = fireOnCall;
    }

    public int NotifyCapturedCount { get; private set; }

    public int ResetCount { get; private set; }

    /// Increments the call count and fires exactly once, on the configured
    /// call — never again afterwards, even if Evaluate keeps being called.
    public bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now)
    {
        _callCount++;
        return _callCount == _fireOnCall;
    }

    public void NotifyCaptured() => NotifyCapturedCount++;

    public void Reset() => ResetCount++;
}
