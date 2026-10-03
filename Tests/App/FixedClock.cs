namespace LoreFetch.Tests.App;

/// A clock stuck at one instant, in UTC, so a run's date-and-time name is the
/// same on every machine (RunStore names new runs from local time).
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
