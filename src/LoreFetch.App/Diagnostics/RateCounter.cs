namespace LoreFetch.App.Diagnostics;

/// <summary>
/// Pure, allocation-free "how many of these happened per second, over the
/// window since the last read" counter. No clock of its own — every method
/// that needs "now" takes it as a parameter, the same pattern
/// <c>ScanPipeline</c> uses for its trigger calls, which is what keeps this
/// testable without a real timer.
/// </summary>
public sealed class RateCounter
{
    private int _count;
    private DateTimeOffset _windowStart;

    public RateCounter(DateTimeOffset now) => _windowStart = now;

    /// <summary>Records one occurrence. Safe to call from any thread.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>
    /// Returns the rate (occurrences / elapsed seconds) for the window that
    /// just ended — measured from whenever this counter was last read (or
    /// constructed, for the first call) up to <paramref name="now"/> — then
    /// RESETS both the count and the window start, so the next call measures
    /// only what happens after this one. Two calls back to back with no
    /// intervening <see cref="Increment"/> therefore return the first
    /// window's rate, then 0 — never a rate that includes stale counts from
    /// before the reset.
    /// </summary>
    /// <remarks>
    /// Uses the ACTUAL elapsed time between reads, not a fixed nominal
    /// window, so a reporting loop that occasionally runs late (a paused
    /// process, a slow tick) still reports a correct rate rather than one
    /// silently deflated by dividing by too small a number.
    /// </remarks>
    public double TakeRateAndReset(DateTimeOffset now)
    {
        var count = Interlocked.Exchange(ref _count, 0);
        var elapsedSeconds = (now - _windowStart).TotalSeconds;
        _windowStart = now;

        // Guards a zero (or negative, from a non-monotonic clock) elapsed
        // window — dividing by it would produce infinity or a nonsensical
        // negative rate instead of the "nothing to report yet" that 0 means.
        return elapsedSeconds > 0 ? count / elapsedSeconds : 0;
    }
}
