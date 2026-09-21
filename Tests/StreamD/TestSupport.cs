using System.Globalization;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Tests.StreamD;

/// Shared row-building helper so every test states only the field(s) it
/// actually cares about, and a fixed timestamp so assertions on
/// `LastScannedAt` are exact rather than "close to now".
internal static class TestSupport
{
    public static readonly DateTimeOffset SampleTimestamp =
        DateTimeOffset.Parse("2026-09-21T10:00:00.0000000+00:00", CultureInfo.InvariantCulture);

    public static CollectionRow Row(
        string oracleId = "11111111-1111-1111-1111-111111111111",
        string oracleName = "Forest",
        int quantity = 1,
        string? condition = null,
        DateTimeOffset? lastScannedAt = null,
        int? bestMatchDistance = 42,
        RowSource source = RowSource.Hash,
        string? artworkId = "22222222-2222-2222-2222-222222222222") =>
        new(oracleId, oracleName, quantity, condition, lastScannedAt ?? SampleTimestamp, bestMatchDistance, source, artworkId);
}

/// Captures formatted log messages so a test can assert "a merge was
/// logged" without wiring a real sink. `Microsoft.Extensions.Logging` gives
/// no first-party in-memory capture type, so this is the minimal
/// implementation of the interface rather than a mock framework dependency.
internal sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
