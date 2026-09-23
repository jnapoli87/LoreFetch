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

            // Packages I1–I3: LOREFETCH_MODE selects Real (default) or
            // Fakes. ResolveCompositionMode never throws — an unrecognised
            // value is logged as an error and falls back to Fakes — so a
            // typo in an environment variable is never the reason the app
            // won't start.
            var mode = AppComposition.ResolveCompositionMode(
                Environment.GetEnvironmentVariable("LOREFETCH_MODE"), logger);

            try
            {
                _session = AppComposition
                    .CreateAsync(mode, loggerFactory, _shutdownCts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex) when (mode == CompositionMode.Real)
            {
                // Real mode must fail LOUDLY rather than silently fall back
                // to Fakes (docs/orchestration-plan.md's I2/I3 override) —
                // a missing or corrupt committed hash index/thresholds file
                // is exactly this path; AppComposition already logged the
                // specific path at Critical before this exception reached
                // here. A camera that simply is not plugged in does NOT
                // reach this catch: WebcamFrameSourceFactory's own
                // FrameSourceException is deferred to the pipeline's
                // SourceFailed event instead (see AppComposition's
                // RealWebcamFrameSourceFactory), which MainWindow renders as
                // a banner once it exists, rather than a crash before it does.
                logger.LogCritical(ex, "Real mode failed to start: {Message}", ex.Message);

                // Deferred via Post, never called synchronously here:
                // OnFrameworkInitializationCompleted runs BEFORE
                // StartWithClassicDesktopLifetime's dispatcher loop actually
                // starts pumping (that happens right after this method
                // returns), so calling desktop.Shutdown(1) directly here
                // throws "Cannot perform requested operation because the
                // Dispatcher shut down" from inside Main once the loop does
                // start — confirmed by hitting it with a renamed index file.
                // Posting queues the shutdown for once the loop is actually
                // running, exactly like MaybeScheduleSmokeExit's own
                // deferred Shutdown() call below.
                Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                return;
            }

            // The one diagnostic line CONTRACTS.md's Logging section exists
            // for: what was actually negotiated, not what was requested.
            logger.LogInformation(
                "LoreFetch started ({Mode} mode). Source: {SourceDescription}",
                mode,
                _session.Pipeline.SourceDescription);

            desktop.MainWindow = new MainWindow(_session);

            // Both events are wired to the same teardown, and AppSession's
            // own DisposeAsync is idempotent (guarded internally), because
            // the two paths are not mutually exclusive. `ShutdownRequested`
            // is raised for a cooperative shutdown (window close, an OS
            // request, or `TryShutdown()`) but NOT for `desktop.Shutdown()`
            // itself, which is `DoShutdown(..., force: true, ...)` — and
            // that `force` skips the `ShutdownRequested` invoke entirely
            // (see ClassicDesktopStyleApplicationLifetime.DoShutdown: the
            // event only fires `if (!force)`). `Exit`, in contrast, fires
            // unconditionally at the end of `DoShutdown` on every path that
            // actually tears down (force or not, cooperative or OS-driven),
            // which is why it — not `ShutdownRequested` — is the one safe
            // place to guarantee cleanup runs.
            //
            // Concretely: this was the smoke-exit leak. `MaybeScheduleSmokeExit`
            // below calls `desktop.Shutdown()`, which never raised
            // `ShutdownRequested`, so the old single-hook version never
            // disposed the session and leaked one
            // `%TEMP%/lorefetch-demo-frames-<guid>` folder per smoke run.
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            MaybeScheduleSmokeExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) => Shutdown();

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => Shutdown();

    private void Shutdown()
    {
        _shutdownCts?.Cancel();

        // Blocking here is acceptable: shutdown is already underway, the
        // session's teardown is bounded (cancel + drain, no real I/O
        // waits), and there is nothing left for the UI thread to do.
        // Safe to call from both handlers above: AppSession.DisposeAsync
        // guards itself so only the first call does any work.
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
