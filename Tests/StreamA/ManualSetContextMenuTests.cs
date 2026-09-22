using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// A10-fix bug 1: "Set card manually…" did nothing in the live exe — no
/// type-ahead ever appeared, so <see cref="TileState.ManuallySet"/> could not
/// be reached by hand.
///
/// <para>
/// The existing A6 <c>TileContextMenu_CarriesTwoItems_SetManuallyAndClear</c>
/// test (<c>TileInteractionTests.cs</c>) only asserts the two
/// <see cref="MenuItem"/> headers exist — it never opens the menu and never
/// raises <c>Click</c>, so it could not have caught this. These tests drive
/// the REAL <see cref="MainWindow"/> the way a user does: a real right-click
/// (<c>MouseDown</c>/<c>MouseUp</c> with <see cref="MouseButton.Right"/>)
/// opens the tile's actual <see cref="ContextMenu"/>, and the real
/// <see cref="MenuItem.ClickEvent"/> is raised on the real menu item found in
/// that open menu's own <c>Items</c> — never a direct call into
/// <see cref="TileViewModel"/>.
/// </para>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class ManualSetContextMenuTests
{
    private const int Good = 100;
    private const int Ok = 200;

    private static RectifiedCard MakeCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    private static Cohort MakeSingleCohort()
    {
        var tile = new CohortTile(MakeCard(), [Candidate("Lightning Bolt", 50)], Good, Ok);
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, CaptureReason.Manual, [tile]);
    }

    private static AppSession MakeSession(IOracleCatalog? catalog = null)
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        return new AppSession(pipeline, source, Task.CompletedTask, settings, catalog: catalog);
    }

    /// <summary>
    /// Finds the tile's outer Grid (80×141, per the DataTemplate in
    /// MainWindow.axaml), right-clicks it via the real headless pointer
    /// pipeline to open its ContextMenu, finds the real "Set card manually…"
    /// MenuItem inside that open menu and raises its real Click event — then
    /// asserts the type-ahead AutoCompleteBox becomes visible AND focused,
    /// and that choosing an entry sets the tile to ManuallySet.
    /// </summary>
    [AvaloniaFact]
    public void RightClick_SetCardManually_OpensFocusedTypeAhead_AndSelectionSetsManuallySet()
    {
        var catalog = new InlineOracleCatalog([new OracleEntry("oracle-lotus", "Black Lotus")]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.Width == 80 && g.Height == 141);
        Assert.NotNull(tileGrid);

        var contextMenu = tileGrid!.ContextMenu;
        Assert.NotNull(contextMenu);

        // Real right-click at the tile's centre, exactly as a user would.
        var center = tileGrid.TranslatePoint(
            new Point(tileGrid.Bounds.Width / 2, tileGrid.Bounds.Height / 2), window);
        Assert.NotNull(center);

        window.MouseDown(center!.Value, MouseButton.Right);
        window.MouseUp(center!.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(contextMenu!.IsOpen, "A real right-click must open the tile's ContextMenu.");

        var menuItem = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header?.ToString() == "Set card manually…");
        Assert.NotNull(menuItem);

        // Raise the REAL Click routed event on the REAL MenuItem instance —
        // exactly what clicking it fires — never TileViewModel directly. A
        // real pointer click also closes the popup as part of Avalonia's own
        // menu-item selection handling (separate from the Click event this
        // raises); do that explicitly here so the screenshot below shows the
        // tile the way a user actually sees it post-click, not mid-click
        // with the popup still covering it.
        menuItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        contextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        var tileVm = vm.Tiles[0];
        Assert.True(tileVm.IsTypeAheadOpen, "IsTypeAheadOpen must be true after clicking 'Set card manually…'.");

        var autoCompleteBox = window.GetVisualDescendants()
            .OfType<AutoCompleteBox>()
            .FirstOrDefault(a => a.DataContext == tileVm);
        Assert.NotNull(autoCompleteBox);
        Assert.True(autoCompleteBox!.IsVisible, "The type-ahead AutoCompleteBox must be visible.");

        // Focus: the box (or its internal TextBox) must actually have focus
        // so the user can type immediately, with no extra click.
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        var focusedInBox = focused is not null &&
            (ReferenceEquals(focused, autoCompleteBox) ||
             focused.GetVisualAncestors().Contains(autoCompleteBox));
        Assert.True(focusedInBox, $"Focus must land in the type-ahead box; actual focus: {focused?.GetType().Name ?? "null"}");

        // Screenshot the open, focused type-ahead overlay — the visible state
        // that was reported as never appearing. Saved to the test output
        // directory, never the repo tree.
        var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        var pngPath = Path.Combine(AppContext.BaseDirectory, "MainWindow-A10Fix-TypeAheadOpen.png");
        using (var stream = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
        {
            bitmap!.Save(stream, PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"Screenshot: {pngPath}");
        ScreenshotAssertions.AssertDimensions(pngPath, 1024, 768);
        ScreenshotAssertions.AssertNotUniformColor(pngPath);

        // Now drive a selection through the real control, the way a user
        // would: type a prefix so the real AsyncPopulator path is exercised...
        autoCompleteBox.Text = "Black";
        Dispatcher.UIThread.RunJobs();

        // ...then set SelectedItem the way choosing a dropdown entry does
        // (AutoCompleteBox sets SelectedItem internally on a pick, which is
        // what raises SelectionChanged — the event OnTypeAheadSelectionChanged
        // handles). The item is built directly rather than by blocking on the
        // populator's Task from this thread: the populator awaits
        // Task.Yield(), and blocking the UI thread's own dispatcher for its
        // continuation would deadlock.
        var lotus = new CatalogItem("oracle-lotus", "Black Lotus");
        autoCompleteBox.SelectedItem = lotus;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TileState.ManuallySet, tileVm.State);
        Assert.Equal("Black Lotus", tileVm.DisplayName);
        Assert.False(tileVm.IsTypeAheadOpen, "Overlay must close after a selection is made.");

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Fakes / helpers
    // -----------------------------------------------------------------------

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

    private sealed class InlineOracleCatalog : IOracleCatalog
    {
        public InlineOracleCatalog(IReadOnlyList<OracleEntry> entries) => All = entries;
        public IReadOnlyList<OracleEntry> All { get; }
    }
}

/*
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests") —
 * filled in after the fix lands; see the A10-fix report.
 */
