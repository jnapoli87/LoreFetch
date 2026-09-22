using System.Collections;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// A10-fix bug 2: the collection view did not reload after a successful
/// Enter commit — the row was actually written (the manual Refresh button
/// always showed it), but nothing told <see cref="CollectionViewModel"/> to
/// re-read the store automatically. These tests drive the real
/// <see cref="MainWindow"/> keyboard path (Space, then Enter) and assert the
/// new row appears in <c>CollectionGrid</c> with no call to Refresh.
///
/// <para>
/// Also covers the ruling that a reload must NOT happen on capture (nothing
/// is written) or on <see cref="CollectionStoreException"/> (nothing
/// committed — the retry banner + retained cohort apply instead), and that a
/// focused layout-selector <see cref="RadioButton"/> or the auto-capture
/// <see cref="CheckBox"/> must not swallow Space/Enter as its own activation
/// gesture nor toggle itself when the window-level handler consumes the key.
/// </para>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class CollectionReloadTests
{
    private const int Good = 100;
    private const int Ok = 200;

    private static RectifiedCard MakeCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance = 50) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    private static Cohort MakeCohort(int count = 1)
    {
        var card = MakeCard();
        var tiles = Enumerable.Range(0, count)
            .Select(i => new CohortTile(card, [Candidate($"Card{i}")], Good, Ok))
            .ToList<CohortTile>();
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, count, CaptureReason.Manual, tiles);
    }

    private static AppSession MakeSession(IScanPipeline? pipeline = null, ICollectionStore? store = null)
    {
        var settings = new ScanSettings();
        var p = pipeline ?? new SpyPipeline();
        var source = new NullFrameSource();
        return new AppSession(p, source, Task.CompletedTask, settings, store: store);
    }

    private static DataGrid FindCollectionGrid(MainWindow window)
    {
        var grid = window.GetVisualDescendants()
            .OfType<DataGrid>()
            .FirstOrDefault(g => g.Name == "CollectionGrid");
        Assert.NotNull(grid);
        return grid!;
    }

    private static int RowCount(DataGrid grid) =>
        grid.ItemsSource is IEnumerable e ? e.Cast<object>().Count() : 0;

    // -----------------------------------------------------------------------
    // Core behaviour: Enter reloads on success, no manual Refresh needed
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_AfterSuccessfulCommit_ReloadsCollectionGrid_WithoutManualRefresh()
    {
        var store = new StubCollectionStore();
        var cohort = MakeCohort(1);
        var pipeline = new SpyPipeline(cohort);
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dataGrid = FindCollectionGrid(window);
        Assert.Equal(0, RowCount(dataGrid));

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(); // drains the reload's async continuation

        Assert.Equal(1, RowCount(dataGrid));

        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Single(rows);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Capture must NOT reload — it writes nothing
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void Space_NeverReloadsCollectionGrid()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-existing", "Existing Card", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));

        var cohort = MakeCohort(1);
        var pipeline = new SpyPipeline(cohort);
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dataGrid = FindCollectionGrid(window);
        // Never refreshed, so the pre-existing store row is not shown yet.
        Assert.Equal(0, RowCount(dataGrid));

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, RowCount(dataGrid));

        window.Close();
    }

    // -----------------------------------------------------------------------
    // CollectionStoreException must NOT reload — nothing committed; the
    // retry banner + retained cohort are the correct response instead.
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_WhenStoreThrowsCollectionStoreException_DoesNotReloadCollectionGrid()
    {
        var store = new StubCollectionStore();
        // Seed a row that already exists in the store BEFORE the failed
        // commit. The grid never called Refresh, so it must still show 0
        // rows after Enter fails — if the catch branch erroneously reloaded
        // (chaos case (b)), this seeded row would leak into the grid even
        // though nothing new was committed, which a store that stays merely
        // empty could never reveal.
        store.Seed(new CollectionRow(
            "oracle-preexisting", "Pre-existing Card", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));
        store.ArmNextCommitToThrow();
        var pipeline = new SpyPipeline();
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeCohort(2));
        Dispatcher.UIThread.RunJobs();

        var dataGrid = FindCollectionGrid(window);
        Assert.Equal(0, RowCount(dataGrid));

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, RowCount(dataGrid));
        Assert.True(vm.HasStoreLock);

        // Only the pre-existing seeded row — the failed commit wrote nothing.
        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal("oracle-preexisting", rows[0].OracleId);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Focus on a layout-selector RadioButton must not swallow Enter, and
    // must not toggle the RadioButton itself.
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_WithFocusOnLayoutRadioButton_StillCommitsAndReloads_AndDoesNotToggleTheRadioButton()
    {
        var store = new StubCollectionStore();
        var pipeline = new SpyPipeline();
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeCohort(1));
        Dispatcher.UIThread.RunJobs();

        var radioButtons = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(rb => rb.GroupName == "ExpectedCount")
            .ToList();
        Assert.Equal(3, radioButtons.Count);

        // ScanSettings.ExpectedCount defaults to 1, so index 1 ("3") starts
        // unchecked — the control most likely to visibly betray a swallowed
        // key by flipping itself.
        var unselected = radioButtons[1];
        Assert.False(unselected.IsChecked == true);

        unselected.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(unselected, window.FocusManager?.GetFocusedElement());

        var dataGrid = FindCollectionGrid(window);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        // Enter reached the window-level handler: committed and reloaded.
        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal(1, RowCount(dataGrid));

        // The focused RadioButton did not treat Enter as its own gesture.
        Assert.False(unselected.IsChecked == true, "Enter must not check the focused RadioButton.");
        Assert.Equal(1, vm.ExpectedCount);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Focus on the Auto-capture CheckBox must not swallow Space, and must
    // not toggle the checkbox itself.
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void Space_WithFocusOnAutoCaptureCheckBox_StillCaptures_AndDoesNotToggleTheCheckBox()
    {
        var cohort = MakeCohort(1);
        var pipeline = new SpyPipeline(cohort);
        var session = MakeSession(pipeline);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;

        var autoToggle = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(cb => cb.Content as string == "Auto");
        Assert.False(autoToggle.IsChecked == true);

        autoToggle.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(autoToggle, window.FocusManager?.GetFocusedElement());

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Space reached the window-level handler: capture happened.
        Assert.Single(vm.Tiles);

        // The focused CheckBox did not treat Space as its own gesture.
        Assert.False(autoToggle.IsChecked == true, "Space must not check the focused Auto checkbox.");
        Assert.False(vm.AutoCaptureEnabled);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    private sealed class SpyPipeline : IScanPipeline
    {
        private readonly Cohort? _captureResult;

#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<FrameSourceException>? SourceFailed;
        public event Action<Cohort>? AutoCaptured;
#pragma warning restore CS0067

        public SpyPipeline(Cohort? captureResult = null) => _captureResult = captureResult;

        public string SourceDescription => "spy";

        public Task<Cohort?> CaptureAsync(CancellationToken ct) => Task.FromResult(_captureResult);

        public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

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
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests") —
 * filled in after the fix lands; see the A10-fix report.
 */
