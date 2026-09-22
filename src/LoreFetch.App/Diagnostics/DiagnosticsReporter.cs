using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LoreFetch.App.Diagnostics;

/// <summary>
/// A10's periodic diagnostic line: every <see cref="Period"/> (10 s), logs
/// preview fps (frames actually blitted), pipeline fps (frames the pipeline
/// processed), managed memory, process working set, and gen 0/1/2
/// collection counts — the flat-memory-over-5-minutes evidence A10's
/// done-when run has to produce.
/// </summary>
/// <remarks>
/// Runs on a background <see cref="Task"/> started in the constructor and
/// stopped by <see cref="DisposeAsync"/>. Uses
/// <see cref="ILogger.LogInformation(string, object[])"/> at Information
/// level — <c>App.axaml.cs</c> configures the root <c>ILoggerFactory</c>
/// with <c>AddConsole()</c>, whose default minimum level is Information, so
/// this line reaches the console sink without any extra configuration.
/// </remarks>
public sealed class DiagnosticsReporter : IAsyncDisposable
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private readonly PreviewDiagnostics _diagnostics;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public DiagnosticsReporter(PreviewDiagnostics diagnostics, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _logger = logger;
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Period, ct).ConfigureAwait(false);

                var sample = _diagnostics.Sample(DateTimeOffset.UtcNow);
                var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                var workingSetBytes = Process.GetCurrentProcess().WorkingSet64;

                _logger.LogInformation(
                    "Diagnostics: preview={PreviewFps:F1}fps pipeline={PipelineFps:F1}fps " +
                    "managedMemory={ManagedBytes}B workingSet={WorkingSetBytes}B " +
                    "gc0={Gen0} gc1={Gen1} gc2={Gen2}",
                    sample.PreviewFramesPerSecond,
                    sample.PipelineFramesPerSecond,
                    managedBytes,
                    workingSetBytes,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — DisposeAsync cancelled the token.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
