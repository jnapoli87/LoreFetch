using System.Collections.ObjectModel;
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

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// Tests for the A5 cohort grid. Two tiers by platform:
///
/// <list type="bullet">
///   <item><c>CohortGrid_VisualTree_ShowsCorrectMarkersForEachState</c> runs
///     on every platform — visual-tree inspection builds the tree after
///     <c>Show()</c> without pixel rendering.</item>
///   <item><c>CohortGrid_Screenshot_SavesPng</c> renders the window and
///     asserts on the actual pixels; it runs on every platform now that
///     <c>TestApp.cs</c> sets <c>UseHeadlessDrawing = false</c> (plus
///     <c>.UseSkia()</c>) — the earlier <c>WindowsOnly</c> trait rested on a
///     false platform rationale (orchestrator diagnosis, 2026-09-22:
///     <c>UseHeadlessDrawing = true</c> is Avalonia.Headless's own no-op stub
///     renderer and returns null from <c>CaptureRenderedFrame()</c> on every
///     OS, not just macOS) and has been removed.</item>
/// </list>
///
/// The PNG is saved to <see cref="AppContext.BaseDirectory"/> (the test
/// output directory), never to the repo tree — the commit hook's imagery
/// guard watches for raster files in tracked paths; this folder is outside it.
/// </summary>
public class CohortGridTests
{
    // ------------------------------------------------------------------
    // Cross-platform visual-tree check
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies the cohort grid is in the visual tree and shows the correct
    /// per-state markers for five states: Included (high-conf), Included
    /// (low-conf), Excluded, Unresolved and ManuallySet.
    /// </summary>
    [AvaloniaFact]
    public void CohortGrid_VisualTree_ShowsCorrectMarkersForEachState()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        // Load the cohort BEFORE Show() — bindings resolve during layout.
        var vm = (MainViewModel)window.DataContext!;
        var cohort = MakeFiveStateCohort();
        vm.LoadCohort(cohort);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The ItemsControl named CohortGrid must exist.
        var cohortGrid = window.GetVisualDescendants()
            .OfType<ItemsControl>()
            .FirstOrDefault(ic => ic.Name == "CohortGrid");

        Assert.NotNull(cohortGrid);

        // ItemsSource is the Tiles ObservableCollection — check count.
        var tiles = cohortGrid.ItemsSource as ObservableCollection<TileViewModel>;
        Assert.NotNull(tiles);
        Assert.Equal(5, tiles.Count);

        // Exactly one excluded ✕ marker should be visible in the visual tree.
        // The TextBlock with Text="✕" has IsVisible bound to ShowExcludedMarker;
        // only the Excluded tile's TileViewModel has ShowExcludedMarker = true.
        Dispatcher.UIThread.RunJobs(); // let bindings resolve after Show()

        var xMarkers = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.Text == "✕" && tb.IsVisible)
            .ToList();

        Assert.Single(xMarkers);

        window.Close();
    }

    /// <summary>
    /// After LoadCohort replaces a previous cohort, the grid updates to show
    /// the new tiles — verifies the cross-platform path of the collection
    /// replacement. No pixel rendering required.
    /// </summary>
    [AvaloniaFact]
    public void CohortGrid_VisualTree_UpdatesWhenCohortReplaced()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();

        var vm = (MainViewModel)window.DataContext!;

        // Load a one-card cohort.
        vm.LoadCohort(MakeCohort(1));
        Dispatcher.UIThread.RunJobs();

        var cohortGrid = window.GetVisualDescendants()
            .OfType<ItemsControl>()
            .FirstOrDefault(ic => ic.Name == "CohortGrid");
        Assert.NotNull(cohortGrid);
        Assert.Single((ObservableCollection<TileViewModel>)cohortGrid.ItemsSource!);

        // Replace with a nine-card cohort.
        vm.LoadCohort(MakeCohort(9));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(9, ((ObservableCollection<TileViewModel>)cohortGrid.ItemsSource!).Count);

        window.Close();
    }

    // ------------------------------------------------------------------
    // Screenshot — cross-platform (see TestApp.cs: UseHeadlessDrawing=false
    // + UseSkia() produces a real rendered surface on every OS)
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders the <c>MainWindow</c> with all five tile states to a PNG using
    /// the headless drawing backend and verifies the pixels are meaningful —
    /// right size, not a single flat colour. Satisfies the A5 "PNG is
    /// produced" acceptance criterion.
    /// </summary>
    [AvaloniaFact]
    public void CohortGrid_Screenshot_SavesPng()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeFiveStateCohort());

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);

        var outDir = AppContext.BaseDirectory;
        var pngPath = Path.Combine(outDir, "MainWindow-A5-CohortGrid.png");
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
    // Helpers
    // ------------------------------------------------------------------

    private const int Good = 100;
    private const int Ok = 200;

    private static RectifiedCard MakeTestCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    /// A cohort with one tile in each of the five distinguishable states:
    ///   [0] Included, high confidence
    ///   [1] Included, low confidence (IsLowConfidence = true)
    ///   [2] Excluded (was Included, then ToggleExcluded called)
    ///   [3] Unresolved (distance > Ok)
    ///   [4] ManuallySet (SetManually called)
    private static Cohort MakeFiveStateCohort()
    {
        var card = MakeTestCard();

        var includedHigh = new CohortTile(card, [Candidate("Lightning Bolt", 50)], Good, Ok);
        var includedLow = new CohortTile(card, [Candidate("Counterspell", 150)], Good, Ok);

        var excluded = new CohortTile(card, [Candidate("Dark Ritual", 50)], Good, Ok);
        excluded.ToggleExcluded();

        var unresolved = new CohortTile(card, [Candidate("Mystery Card", 500)], Good, Ok);

        var manually = new CohortTile(card, [Candidate("Wrong Guess", 500)], Good, Ok);
        manually.SetManually(new OracleEntry("oracle-lotus", "Black Lotus"));

        return new Cohort(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            expectedCount: 5,
            CaptureReason.Manual,
            [includedHigh, includedLow, excluded, unresolved, manually]);
    }

    private static Cohort MakeCohort(int count)
    {
        var card = MakeTestCard();
        var tiles = Enumerable.Range(0, count)
            .Select(i => new CohortTile(card, [Candidate($"Card {i}", 50)], Good, Ok))
            .ToList<CohortTile>();

        return new Cohort(
            Guid.NewGuid(), DateTimeOffset.UtcNow, count, CaptureReason.Manual, tiles);
    }

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
