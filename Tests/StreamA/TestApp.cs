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
            // UseSkia() registers the Skia drawing backend — even in headless mode,
            // UseHeadlessDrawing=true still needs a raster drawing engine to produce
            // the in-memory bitmap that CaptureRenderedFrame() returns.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // UseHeadlessDrawing=true renders to an in-memory surface so
                // CaptureRenderedFrame() returns a non-null RenderTargetBitmap.
                UseHeadlessDrawing = true,

                // ShouldRenderOnUIThread=true makes the renderer run synchronously
                // on the UI thread, so CaptureRenderedFrame() can return the frame
                // in the same tick rather than racing a background render thread.
                ShouldRenderOnUIThread = true,
            });
}
