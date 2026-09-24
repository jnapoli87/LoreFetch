using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// A10-fix bug 4: relabel the layout selector. The radio buttons read
/// "1 card" / "3 × 1" / "3 × 3" (replacing the bare "1" / "3" / "9"), and the
/// prefix label reads "Layout:" instead of "Count:". The VALUES written to
/// <see cref="ScanSettings.ExpectedCount"/> are unchanged — still 1, 3, 9 —
/// only the display text changed.
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class LayoutSelectorLabelTests
{
    [AvaloniaFact]
    public void LayoutSelector_HasLayoutPrefixLabel_NotCount()
    {
        var window = MakeWindow();

        var labels = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(tb => tb.Text)
            .ToList();

        Assert.Contains("Layout:", labels);
        Assert.DoesNotContain("Count:", labels);

        window.Close();
    }

    [AvaloniaFact]
    public void LayoutSelector_RadioButtons_ReadOneCardThreeByOneThreeByThree()
    {
        var window = MakeWindow();

        var radioButtons = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(rb => rb.GroupName == "ExpectedCount")
            .OrderBy(rb => rb.Bounds.X)
            .ToList();

        Assert.Equal(3, radioButtons.Count);
        Assert.Equal("1 card", radioButtons[0].Content as string);
        Assert.Equal("3 × 1", radioButtons[1].Content as string);
        Assert.Equal("3 × 3", radioButtons[2].Content as string);

        // The old bare labels must be gone.
        var contents = radioButtons.Select(rb => rb.Content as string).ToList();
        Assert.DoesNotContain("1", contents);
        Assert.DoesNotContain("3", contents);
        Assert.DoesNotContain("9", contents);

        window.Close();
    }

    /// <summary>
    /// Relabelling must not touch the values written through to
    /// <see cref="ScanSettings"/> — checking the "3 × 1" button still sets
    /// <c>ExpectedCount = 3</c>, and "3 × 3" still sets <c>9</c>.
    /// </summary>
    [AvaloniaFact]
    public void LayoutSelector_CheckingThreeByOne_StillWritesExpectedCountThree()
    {
        var window = MakeWindow();
        var vm = (MainViewModel)window.DataContext!;

        var radioButtons = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(rb => rb.GroupName == "ExpectedCount")
            .OrderBy(rb => rb.Bounds.X)
            .ToList();

        var threeByOne = radioButtons[1];
        Assert.Equal("3 × 1", threeByOne.Content as string);

        threeByOne.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.ExpectedCount);

        var threeByThree = radioButtons[2];
        Assert.Equal("3 × 3", threeByThree.Content as string);

        threeByThree.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(9, vm.ExpectedCount);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MainWindow MakeWindow()
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        var session = new AppSession(pipeline, source, Task.CompletedTask, settings);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private sealed class NullScanPipeline : IScanPipeline
    {
#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<Cohort>? AutoCaptured;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067
        public string SourceDescription => "headless-test";
        public Task<Cohort?> CaptureAsync(CancellationToken ct) => Task.FromResult<Cohort?>(null);
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public string Description => "headless-test";
        public FrameGeometry Geometry => new(1, 1, 0);
        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct) => EmptyAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static async IAsyncEnumerable<CameraFrame> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

/*
 * Chaos-test results (see docs/TESTING.md "Standing practice: chaos-test every regression test") —
 * filled in after the fix lands; see the A10-fix report.
 */
