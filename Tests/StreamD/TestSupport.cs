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

/// Same capture behaviour as <see cref="CapturingLogger"/>, typed for
/// <c>ILogger&lt;T&gt;</c> because <see cref="Collection.CsvCollectionStore"/>'s
/// constructor requires the generic interface, not the untyped one.
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// A directory under the OS temp path, unique per instance and deleted on
/// dispose — every store test gets its own, so tests never race each other
/// over one collection.csv and never write anywhere under the repo tree.
internal sealed class TempWorkspace : IDisposable
{
    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "LoreFetch.Tests.StreamD", Guid.NewGuid().ToString("N"));

    public string CollectionPath => Path.Combine(Directory, "collection.csv");

    public TempWorkspace() => System.IO.Directory.CreateDirectory(Directory);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only — a locked handle left over from a
            // test that intentionally held the file open must not fail the
            // whole run.
        }
    }
}
