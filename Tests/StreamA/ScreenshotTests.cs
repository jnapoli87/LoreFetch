using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Headless UI tests for the A4 controls — expected-count selector and
/// auto-capture toggle.
///
/// Two tiers by platform:
///   - <c>MainWindow_SelectorAndToggleAreVisibleInVisualTree</c> runs on
///     every platform because visual-tree inspection requires no pixel
///     rendering — the layout engine builds the tree after <c>Show()</c>
///     even without a headless drawing surface.
///   - <c>MainWindow_RendersSelector_AndToggleIsVisible_Screenshot</c> is
///     tagged <c>WindowsOnly</c>. <c>CaptureRenderedFrame()</c> needs the
///     headless drawing backend; on macOS (ARM64) the in-memory surface is
///     not produced in this headless context, the same platform split as the
///     golden-hash test. The test WILL run on the win-x64 CI leg and produce
///     the PNG required by the A4 acceptance criteria.
///
/// The PNG is saved to <see cref="AppContext.BaseDirectory"/> (the test
/// output directory), never to the repo tree — the commit hook's imagery
/// guard watches for raster files in tracked paths; this folder is outside it.
public class ScreenshotTests
{
    // ------------------------------------------------------------------
    // Visual-tree check — runs on every platform
    // ------------------------------------------------------------------

    /// Verifies the A4 controls are in the MainWindow visual tree after Show().
    /// No pixel rendering required: the layout engine commits the visual tree
    /// synchronously, so controls have IsVisible=true and are findable by type.
    [AvaloniaFact]
    public void MainWindow_SelectorAndToggleAreVisibleInVisualTree()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();

        // Process any pending layout jobs — layout is synchronous but some
        // initial jobs may be queued. After this, controls are measured and
        // in the visual tree.
        Dispatcher.UIThread.RunJobs();

        // Assert: all three count-selector RadioButtons are in the visual tree
        var radioButtons = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(rb => rb.GroupName == "ExpectedCount")
            .ToList();

        Assert.Equal(3, radioButtons.Count);
        Assert.True(
            radioButtons.All(rb => rb.IsVisible),
            "All three count-selector radio buttons must be visible.");

        // Assert: the auto-capture CheckBox is in the visual tree
        var autoToggle = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(cb => cb.Content as string == "Auto");

        Assert.NotNull(autoToggle);
        Assert.True(autoToggle.IsVisible, "Auto-capture toggle must be visible.");

        window.Close();
    }

    // ------------------------------------------------------------------
    // Screenshot — WindowsOnly (headless drawing surface; macOS excluded
    // for the same reason as the golden-hash tests: INTER_AREA and the
    // headless render backend behave differently on ARM64 macOS)
    // ------------------------------------------------------------------

    /// Renders the MainWindow to a PNG using the headless drawing backend and
    /// verifies it is non-empty. Produces the PNG file that satisfies the A4
    /// "PNG is produced" acceptance criterion.
    ///
    /// Traited <c>WindowsOnly</c> — the headless in-memory surface is only
    /// produced on win-x64 in this project's configuration, the same platform
    /// split as <see cref="Avalonia.Headless.AvaloniaHeadlessPlatformOptions.UseHeadlessDrawing"/>.
    [Trait("Category", "WindowsOnly")]
    [AvaloniaFact]
    public void MainWindow_RendersSelector_AndToggleIsVisible_Screenshot()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();

        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);

        // Save to the test output directory — never to the repo tree.
        var outDir = AppContext.BaseDirectory;
        var pngPath = Path.Combine(outDir, "MainWindow-A4.png");
        using (var stream = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
        {
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }

        Console.WriteLine($"Screenshot: {pngPath}");

        var info = new FileInfo(pngPath);
        Assert.True(info.Exists, $"Screenshot PNG must exist at: {pngPath}");
        Assert.True(info.Length > 0, "Screenshot must be non-empty.");

        window.Close();
    }

    // ------------------------------------------------------------------
    // Session factory and test doubles
    // ------------------------------------------------------------------

    private static AppSession MakeSession()
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        return new AppSession(pipeline, source, Task.CompletedTask, settings);
    }

    private sealed class NullScanPipeline : IScanPipeline
    {
#pragma warning disable CS0067 // Events required by the interface but never raised by this stub
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<Cohort>? AutoCaptured;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067

        public string SourceDescription => "headless-test";

        public Task<Cohort?> CaptureAsync(CancellationToken ct) =>
            Task.FromResult<Cohort?>(null);

        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public string Description => "headless-test";

        public FrameGeometry Geometry => new(1, 1, 0);

        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct) =>
            EmptyAsync();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<CameraFrame> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
