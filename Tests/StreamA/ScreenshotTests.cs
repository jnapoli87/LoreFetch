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
///   - <c>MainWindow_RendersSelector_AndToggleIsVisible_Screenshot</c> renders
///     the window and asserts on the actual pixels. It runs on every
///     platform: with <c>TestApp.cs</c>'s <c>UseHeadlessDrawing = false</c>
///     (plus <c>.UseSkia()</c>), the headless backend produces a real
///     rendered surface everywhere, not just on win-x64 — the earlier
///     <c>WindowsOnly</c> trait rested on a false platform rationale
///     (orchestrator diagnosis, 2026-09-22: <c>UseHeadlessDrawing = true</c>
///     is Avalonia.Headless's own no-op stub renderer, which returns null
///     from <c>CaptureRenderedFrame()</c> on every OS, not just macOS —
///     verified failing on win-x64 too before the fix) and has been removed.
///     The test still produces the PNG required by the A4 acceptance
///     criteria; it just no longer needs a CI-leg split to do it.
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
    // Screenshot — cross-platform (see TestApp.cs: UseHeadlessDrawing=false
    // + UseSkia() produces a real rendered surface on every OS)
    // ------------------------------------------------------------------

    /// Renders the MainWindow to a PNG using the headless drawing backend and
    /// verifies the pixels are meaningful — not merely non-null, but the
    /// right size and not a single flat colour (which is what a broken or
    /// unrendered surface would still pass a bare not-null check with).
    /// Produces the PNG file that satisfies the A4 "PNG is produced"
    /// acceptance criterion.
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
        ScreenshotAssertions.AssertDimensions(pngPath, 1024, 768);
        ScreenshotAssertions.AssertNotUniformColor(pngPath);

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
