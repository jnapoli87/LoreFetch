using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// A10-fix bug 3: collection table polish.
///
/// <list type="bullet">
///   <item>Every DataGrid header must be FULLY visible at the default window
///     size — <c>Qty</c> did not render at all; <c>Condition</c>, <c>Source</c>
///     and <c>Dist</c> were clipped ("Condi", "Sou", "D"); <c>Name</c> took
///     roughly half the width.</item>
///   <item><c>LastScannedAt</c> must display in LOCAL time, unambiguously
///     (24-hour, <c>yyyy-MM-dd HH:mm:ss</c>) — the stored value stays UTC.</item>
///   <item>The header row must match the dark theme (it rendered light).</item>
/// </list>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class CollectionTablePolishTests
{
    // -----------------------------------------------------------------------
    // Local-time conversion — fixed TimeZoneInfo, not the machine's own
    // -----------------------------------------------------------------------

    /// <summary>
    /// A custom, always-available, fixed-offset zone (-05:00) — deliberately
    /// NOT an IANA/Windows zone id (those differ between Windows and Linux,
    /// e.g. "Pacific Standard Time" vs "America/Los_Angeles"), so this test
    /// is deterministic on every CI leg regardless of the machine's own zone
    /// or OS.
    /// </summary>
    private static readonly TimeZoneInfo FixedZone =
        TimeZoneInfo.CreateCustomTimeZone("Fixed-05:00-Test", TimeSpan.FromHours(-5), "Fixed -5 (test)", "Fixed -5 (test)");

    [Fact]
    public void CollectionRowItem_LastScannedLocalText_ConvertsUtcToFixedZone_Unambiguous24Hour()
    {
        // Stored value is UTC, as DECISIONS.md's storage contract requires.
        var storedUtc = new DateTimeOffset(2026, 9, 22, 18, 30, 45, TimeSpan.Zero);
        var row = new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 1, null, storedUtc, 50, RowSource.Hash, null);

        var item = new CollectionRowItem(row, FixedZone);

        // 18:30:45 UTC − 5h = 13:30:45 local, 24-hour, zero-padded.
        Assert.Equal("2026-09-22 13:30:45", item.LastScannedLocalText);

        // The stored value itself must be untouched — still UTC.
        Assert.Equal(storedUtc, item.Row.LastScannedAt);
        Assert.Equal(TimeSpan.Zero, item.Row.LastScannedAt.Offset);
    }

    [Fact]
    public void CollectionRowItem_LastScannedLocalText_CrossesMidnightIntoThePreviousDay()
    {
        // 02:00 UTC minus 5h crosses into the previous calendar day — the
        // conversion must carry the date, not just the clock time.
        var storedUtc = new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
        var row = new CollectionRow(
            "oracle-x", "Test Card", 1, null, storedUtc, null, RowSource.Manual, null);

        var item = new CollectionRowItem(row, FixedZone);

        Assert.Equal("2026-09-21 21:00:00", item.LastScannedLocalText);
    }

    [Fact]
    public async Task CollectionViewModel_LoadAsync_UsesInjectedDisplayTimeZone_ForEveryRow()
    {
        var storedUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow("oracle-a", "Card A", 1, null, storedUtc, 50, RowSource.Hash, null));
        store.Seed(new CollectionRow("oracle-b", "Card B", 2, null, storedUtc, null, RowSource.Manual, null));

        var vm = new CollectionViewModel(store, exporters: null, displayTimeZone: FixedZone);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal("2026-01-01 07:00:00", r.LastScannedLocalText));
    }

    // -----------------------------------------------------------------------
    // Header visibility + dark theme — through the real MainWindow
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renders the real <c>CollectionGrid</c> with a few seeded rows at the
    /// default window size and asserts, per header column:
    /// <list type="bullet">
    ///   <item>the header's own inner <c>TextBlock</c> is not clipped — its
    ///     laid-out width is enough for its <c>DesiredSize</c> (the width it
    ///     would need to show the FULL header text unclipped). This is what
    ///     "Qty" not rendering and "Condi"/"Sou"/"D" being truncated actually
    ///     were: the column's declared width left less room than the header
    ///     text needed.</item>
    ///   <item>the header row's own background is dark (matching the rest of
    ///     the window), not Fluent's light default.</item>
    /// </list>
    /// Also produces the PNG screenshot required by DECISIONS.md's UI-screenshot
    /// standing practice, and asserts <c>LastScannedAt</c> renders as an
    /// unambiguous local 24-hour timestamp rather than the raw UTC
    /// <c>DateTimeOffset</c> representation.
    /// </summary>
    [AvaloniaFact]
    public async Task CollectionGrid_Screenshot_HeadersNotClipped_DarkThemed_LocalTimeFormatted()
    {
        var store = new StubCollectionStore();
        var utc = new DateTimeOffset(2026, 9, 22, 18, 30, 45, TimeSpan.Zero);
        store.Seed(new CollectionRow("oracle-bolt", "Lightning Bolt", 3, null, utc, 42, RowSource.Hash, "artwork-1"));
        store.Seed(new CollectionRow("oracle-lotus", "Black Lotus", 1, null, utc, null, RowSource.Manual, null));
        store.Seed(new CollectionRow("oracle-forest", "Forest", 12, null, utc, 8, RowSource.Hash, "artwork-2"));

        var session = MakeSession(store);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Load through the Refresh path (same as a live user clicking it) —
        // CollectionPanel.DataContext is the CollectionViewModel MainWindow
        // built from the session's store, using TimeZoneInfo.Local (the
        // fixed-zone conversion itself is covered above; this test only
        // checks the FORMAT is local/unambiguous, not a specific machine's
        // offset).
        var refreshButton = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.Content as string == "Refresh");
        refreshButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20); // let the fire-and-forget LoadAsync complete
        Dispatcher.UIThread.RunJobs();

        var dataGrid = window.GetVisualDescendants()
            .OfType<DataGrid>()
            .First(g => g.Name == "CollectionGrid");
        Assert.Equal(3, ((System.Collections.IEnumerable)dataGrid.ItemsSource!).Cast<object>().Count());

        // --- Header clipping check -----------------------------------------
        var expectedHeaders = new[] { "Name", "Qty", "Condition", "Source", "Dist", "Last Scanned" };
        var headers = window.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Where(h => h.Content is string s && expectedHeaders.Contains(s))
            .ToList();
        Assert.Equal(expectedHeaders.Length, headers.Count);

        foreach (var header in headers)
        {
            var textBlock = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.NotNull(textBlock);

            // TextBlock.DesiredSize is NOT useful here: it was measured with
            // the column's own constrained width as the available space, so
            // it comes back already clamped to whatever fit — comparing it
            // against Bounds.Width is vacuous (they're both the same
            // constrained number). Instead, measure the header string's own
            // NATURAL (unconstrained) width independently via TextLayout,
            // using the same font the header actually renders with, and
            // compare THAT against the space the TextBlock was actually
            // given. This is what actually distinguishes "fits" from
            // "silently trimmed to fewer characters" — exactly the reported
            // "Qty" / "Condi" / "Sou" / "D" symptom.
            var typeface = new Typeface(textBlock!.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch);
            var naturalLayout = new TextLayout(textBlock.Text ?? string.Empty, typeface, textBlock.FontSize, textBlock.Foreground);

            Assert.True(
                naturalLayout.Width <= textBlock.Bounds.Width + 0.5,
                $"Header '{header.Content}' is clipped: needs {naturalLayout.Width:F1}px unclipped " +
                $"but was only given {textBlock.Bounds.Width:F1}px.");
        }

        // --- Dark-theme header check ----------------------------------------
        // Sample a pixel inside the first header's own background (a corner,
        // away from the glyph ink and the sort-arrow area) and assert it is
        // dark rather than Fluent's light-theme default (near-white).
        var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);

        var firstHeader = headers.OrderBy(h => h.Bounds.X).First();
        var corner = firstHeader.TranslatePoint(new Avalonia.Point(4, 4), window);
        Assert.NotNull(corner);
        var luma = SampleLuma(bitmap!, (int)corner!.Value.X, (int)corner.Value.Y);
        Assert.True(luma < 128, $"Header background must be dark (sampled luma {luma}, expected < 128).");

        // --- Local-time formatting check -------------------------------------
        var lastScannedTexts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(tb => tb.Text)
            .Where(t => t is not null && Regex.IsMatch(t, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"))
            .ToList();
        Assert.Equal(3, lastScannedTexts.Count); // one per seeded row

        // --- Save the PNG (never into the repo tree — the test output dir) --
        var outDir = AppContext.BaseDirectory;
        var pngPath = Path.Combine(outDir, "MainWindow-A10Fix-CollectionTable.png");
        using (var stream = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
        {
            bitmap!.Save(stream, PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"Screenshot: {pngPath}");
        ScreenshotAssertions.AssertDimensions(pngPath, 1024, 768);
        ScreenshotAssertions.AssertNotUniformColor(pngPath);

        window.Close();
    }

    private static double SampleLuma(Bitmap bitmap, int x, int y)
    {
        var size = bitmap.PixelSize;
        x = Math.Clamp(x, 0, size.Width - 1);
        y = Math.Clamp(y, 0, size.Height - 1);

        const int stride = 4; // Bgra8888, 1x1 sample
        var handle = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride);
        try
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), handle, stride, stride);
            var b = System.Runtime.InteropServices.Marshal.ReadByte(handle, 0);
            var g = System.Runtime.InteropServices.Marshal.ReadByte(handle, 1);
            var r = System.Runtime.InteropServices.Marshal.ReadByte(handle, 2);
            return 0.114 * b + 0.587 * g + 0.299 * r;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(handle);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AppSession MakeSession(ICollectionStore store)
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        return new AppSession(pipeline, source, Task.CompletedTask, settings, store: store);
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
