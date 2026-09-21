using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace LoreFetch.App;

public partial class App : Application
{
    private CancellationTokenSource? _shutdownCts;
    private AppSession? _session;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // DevTools (AvaloniaUI.DiagnosticsSupport / this.AttachDeveloperTools())
    // is deliberately NOT called here. Resolving its licence was this
    // package's first task: docs.avaloniaui.net's own developer-tools
    // installation guide (checked 2026-09-21) says activation needs
    // "AvaloniaUI Portal credentials that were used to license the tool",
    // and the Avalonia 12 breaking-changes doc says the tools bundled with
    // Avalonia Plus (or higher) should be used instead — both point at a
    // paid licence. That is against one archived repo's README claiming a
    // free Community tier covering "all features available in the legacy
    // Avalonia.Diagnostics package". Current official docs win over an
    // archived repo's claim, so this project — which holds no such licence
    // — leaves DevTools uncalled. Nothing here is redistributed, so this is
    // a dev-time convenience only, not a licence obligation on the shipped
    // app: no functional loss, just no in-process inspector.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger("LoreFetch.App");
            _shutdownCts = new CancellationTokenSource();

            // Fakes only, per the global A override — Real is added at
            // integration once streams B/C/D land (AppComposition's doc
            // comment on CompositionMode).
            _session = AppComposition
                .CreateAsync(CompositionMode.Fakes, loggerFactory, _shutdownCts.Token)
                .GetAwaiter()
                .GetResult();

            // The one diagnostic line CONTRACTS.md's Logging section exists
            // for: what was actually negotiated, not what was requested.
            logger.LogInformation(
                "LoreFetch started (Fakes mode). Source: {SourceDescription}",
                _session.Pipeline.SourceDescription);

            desktop.MainWindow = new MainWindow(_session);
            desktop.ShutdownRequested += OnShutdownRequested;

            MaybeScheduleSmokeExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _shutdownCts?.Cancel();

        // Blocking here is acceptable: shutdown is already underway, the
        // session's teardown is bounded (cancel + drain, no real I/O
        // waits), and there is nothing left for the UI thread to do.
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// Lets a build exit itself after a fixed delay when
    /// LOREFETCH_SMOKE_EXIT_MS is set, so a smoke test can start the exe,
    /// confirm it stayed up and logged cleanly, and let it close on its
    /// own — rather than leaving a window someone has to kill by hand. No
    /// effect unless the variable is set, so normal use is unchanged.
    private static void MaybeScheduleSmokeExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var raw = Environment.GetEnvironmentVariable("LOREFETCH_SMOKE_EXIT_MS");
        if (!int.TryParse(raw, out var delayMs) || delayMs <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        });
    }
}
