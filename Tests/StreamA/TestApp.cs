using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(LoreFetch.Tests.StreamA.HeadlessTestApp))]

namespace LoreFetch.Tests.StreamA;

/// Minimal application host for Avalonia.Headless.XUnit tests. The real
/// <c>LoreFetch.App.App</c> is not used here — it would start the composition
/// root and open the main window, which are outside the headless test surface.
/// This class loads what controls need: the Fluent theme (so RadioButtons,
/// CheckBoxes etc. have styles and hit-test geometry) and the DataGrid Fluent
/// theme (so the A8 collection DataGrid has styles in the visual-tree tests).
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // A10-fix bug 3: match the real App.axaml's RequestedThemeVariant="Dark"
        // so headless screenshot/visual-tree tests see the same themed chrome
        // (DataGrid header, ContextMenu, etc.) the live exe does, rather than
        // Fluent's light default.
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // Add the Fluent theme so compiled-AXAML controls in MainWindow have
        // styles and can participate in layout. Without it, controls have no
        // size and CaptureRenderedFrame returns a blank surface.
        Styles.Add(new FluentTheme());

        // A8: add the DataGrid Fluent theme so CollectionGrid has styles in
        // headless tests. The base URI is the DataGrid package's own avares
        // root; the Source points at its theme file.
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            // UseSkia() registers the real Skia drawing backend, which is what
            // actually produces pixels for CaptureRenderedFrame() to return.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // Orchestrator diagnosis (2026-09-22): UseHeadlessDrawing=true
                // is Avalonia.Headless's OWN no-op stub renderer — a
                // NullRenderer-style drawing context that never touches Skia and
                // never populates a real surface, regardless of platform. That is
                // why CaptureRenderedFrame() returned null here even on win-x64:
                // there was never a rendered frame to capture, on any OS. The old
                // comment claiming this "renders to an in-memory surface" and that
                // the failure was "absent on macOS ARM64" was wrong — the
                // behaviour is identical on every platform, and it was verified
                // failing on Windows too before this fix. Setting it to false
                // routes drawing through the real Skia backend (UseSkia() above),
                // which is what makes CaptureRenderedFrame() return actual pixels.
                UseHeadlessDrawing = false,

                // ShouldRenderOnUIThread=true makes the renderer run synchronously
                // on the UI thread, so CaptureRenderedFrame() can return the frame
                // in the same tick rather than racing a background render thread.
                ShouldRenderOnUIThread = true,
            });
}
