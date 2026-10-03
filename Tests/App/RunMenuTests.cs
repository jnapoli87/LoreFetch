using System.Collections;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// Issue #20 at the window: the collection panel shows the open run from
/// startup, the title and header name it, and the File menu switches runs —
/// never while a cohort is pending, and never to a file that isn't a run.
/// Runs here sit over the real <see cref="CsvCollectionStore"/> in a temp
/// folder; the file pickers and the rename dialog are bypassed by calling
/// the window's own <c>internal</c> run methods, which the menu items call.
/// The "through the menu" tests click the real menu items instead; headless
/// windows get Avalonia's no-op storage provider and launcher, so the open
/// picker returns no file and Show in folder launches nothing.
/// </summary>
public sealed class RunMenuTests : IDisposable
{
    private const int Good = 100;
    private const int Ok = 200;
    private const string FirstRun = "2026-10-02 14-30";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lorefetch-runmenu-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private RunStore NewRuns() =>
        new(Path.Combine(_root, "Runs"),
            path => new CsvCollectionStore(path, NullLogger<CsvCollectionStore>.Instance),
            new FixedClock(new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.Zero)));

    private static Cohort MakeCohort(params string[] names)
    {
        var card = new RectifiedCard([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);
        var tiles = names
            .Select(n => new CohortTile(card, [new CardCandidate("oracle-" + n, n, 50, ArtworkId: null)], Good, Ok))
            .ToList<CohortTile>();
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, tiles.Count, CaptureReason.Manual, tiles);
    }

    private static MainWindow Open(ICollectionStore store, RunStore? runs = null, Cohort? captureResult = null)
    {
        var session = new AppSession(new SpyPipeline(captureResult), new NullFrameSource(), Task.CompletedTask,
            new ScanSettings(), store: store, exporters: [new StubCollectionExporter(new ExportFormat(
                "stub", "Stub Export", ".txt", IsVerified: true, Notes: null))], runs: runs);
        var window = new MainWindow(session) { Width = 1024, Height = 768 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static int GridRows(MainWindow window)
    {
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "CollectionGrid");
        return grid.ItemsSource is IEnumerable e ? e.Cast<object>().Count() : 0;
    }

    private static string Header(MainWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CollectionHeaderText").Text!;

    private static MainViewModel Vm(MainWindow window) => (MainViewModel)window.DataContext!;

    // The real store finishes its read on the thread pool, so the grid's
    // reload lands a few dispatcher turns later.
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met within 2 s.");
    }

    // Space captures the spy's cohort, Enter commits it to the open run.
    private static async Task CaptureAndCommit(MainWindow window)
    {
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Vm(window).HasPendingCohort);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Eventually(() => !Vm(window).HasPendingCohort);
    }

    [AvaloniaFact]
    public void Startup_ShowsTheStoresRows_WithoutWaitingForACommit()
    {
        // The reported bug: rows already in the store stayed hidden until the
        // first commit's reload brought them all back at once.
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow("oracle-a", "Opt", 1, null, DateTimeOffset.UtcNow, 50, RowSource.Hash, null));
        store.Seed(new CollectionRow("oracle-b", "Forest", 3, null, DateTimeOffset.UtcNow, 8, RowSource.Hash, null));

        var window = Open(store);

        Assert.Equal(2, GridRows(window));
        Assert.Equal("Collection · 4 cards", Header(window));
        Assert.Equal("LoreFetch", window.Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Startup_NamesTheFreshRun_AndCommitsCountInTheHeader()
    {
        var runs = NewRuns();
        var window = Open(runs, runs, MakeCohort("Opt", "Opt"));

        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);
        Assert.Equal($"Run: {FirstRun} · 0 cards", Header(window));
        Assert.Equal(0, GridRows(window));

        await CaptureAndCommit(window);
        await Eventually(() => GridRows(window) == 1);

        Assert.Equal($"Run: {FirstRun} · 2 cards", Header(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task NewRun_StartsEmpty_AndOpeningTheEarlierRunShowsItsRowsAgain()
    {
        var runs = NewRuns();
        var window = Open(runs, runs, MakeCohort("Opt"));
        await CaptureAndCommit(window);
        await Eventually(() => GridRows(window) == 1);
        var firstPath = runs.Path;

        await window.StartNewRunAsync();
        await Eventually(() => GridRows(window) == 0);

        Assert.Equal($"LoreFetch — {FirstRun} (2)", window.Title);
        Assert.Equal($"Run: {FirstRun} (2) · 0 cards", Header(window));

        await window.OpenRunAsync(firstPath);
        await Eventually(() => GridRows(window) == 1);

        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);
        Assert.False(Vm(window).HasNotice);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SwitchingRuns_WithACohortPending_IsRefused_AndKeepsBothTheRunAndTheCohort()
    {
        var runs = NewRuns();
        var window = Open(runs, runs, MakeCohort("Opt"));
        var vm = Vm(window);
        vm.LoadCohort(MakeCohort("Shock"));
        var before = runs.Path;

        await window.StartNewRunAsync();
        Assert.Equal(before, runs.Path);
        Assert.True(vm.HasNotice);
        Assert.True(vm.HasPendingCohort);

        vm.NoticeMessage = null;
        await window.RenameRunAsync("Binder");
        Assert.Equal(before, runs.Path);
        Assert.True(vm.HasNotice);

        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningAFileThatIsNotARun_ShowsANotice_AndKeepsTheOpenRun()
    {
        var runs = NewRuns();
        var window = Open(runs, runs, MakeCohort("Opt"));
        await CaptureAndCommit(window);
        await Eventually(() => GridRows(window) == 1);

        Directory.CreateDirectory(_root);
        var export = Path.Combine(_root, "moxfield.csv");
        await File.WriteAllTextAsync(export, "Count,Name,Edition\n1,Opt,\n", TestContext.Current.CancellationToken);

        await window.OpenRunAsync(export);

        Assert.True(Vm(window).HasNotice);
        Assert.Contains("isn't a LoreFetch run", Vm(window).NoticeMessage, StringComparison.Ordinal);
        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);
        Assert.Equal(1, GridRows(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Rename_RetitlesTheRun_AndABadNameExplainsItself()
    {
        var runs = NewRuns();
        var window = Open(runs, runs);

        await window.RenameRunAsync("Trade box");
        Assert.Equal("LoreFetch — Trade box", window.Title);
        Assert.Equal("Run: Trade box · 0 cards", Header(window));

        await window.RenameRunAsync("   ");
        Assert.Equal("Enter a name for the run.", Vm(window).NoticeMessage);
        Assert.Equal("LoreFetch — Trade box", window.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void WithoutARunFile_TheRunCommandsAreDisabled_ButExportIsNot()
    {
        var window = Open(new StubCollectionStore());

        foreach (var name in new[] { "NewRunMenuItem", "OpenRunMenuItem", "RecentRunsMenuItem", "RenameRunMenuItem", "ShowRunInFolderMenuItem" })
            Assert.False(window.FindControl<MenuItem>(name)!.IsEnabled, name);

        var export = window.FindControl<MenuItem>("ExportMenuItem")!;
        Assert.True(export.IsEnabled);
        Assert.Single(export.Items);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_WhileTheFileMenuIsOpen_DoesNotDiscardTheCohort()
    {
        var window = Open(new StubCollectionStore());
        var vm = Vm(window);
        vm.LoadCohort(MakeCohort("Opt"));

        var menu = window.FindControl<Menu>("MainMenu")!;
        menu.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasPendingCohort, "Escape closes the menu; it must not also discard the cohort.");
        window.Close();
    }

    // -----------------------------------------------------------------------
    // Through the menu: the clicks reach the commands above
    // -----------------------------------------------------------------------

    private static void Click(Control control) =>
        control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));

    private static MenuItem Item(MainWindow window, string name) => window.FindControl<MenuItem>(name)!;

    private static IReadOnlyList<MenuItem> OpenRecentList(MainWindow window)
    {
        // As a click does: the menu bar opens, then File's submenu, which
        // raises SubmenuOpened and lists the runs; closing the bar closes it.
        var menu = window.FindControl<Menu>("MainMenu")!;
        menu.Open();
        Item(window, "FileMenu").IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var items = Item(window, "RecentRunsMenuItem").Items.Cast<MenuItem>().ToList();
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
        return items;
    }

    [AvaloniaFact]
    public async Task OpenRecent_ListsEarlierRuns_AndClickingOneOpensIt()
    {
        var runs = NewRuns();
        var window = Open(runs, runs, MakeCohort("Opt"));

        var none = Assert.Single(OpenRecentList(window));
        Assert.False(none.IsEnabled, "With no earlier runs the list says so, and that entry does nothing.");

        await CaptureAndCommit(window);
        await Eventually(() => GridRows(window) == 1);
        Click(Item(window, "NewRunMenuItem"));
        await Eventually(() => GridRows(window) == 0);
        Assert.Equal($"LoreFetch — {FirstRun} (2)", window.Title);

        var earlier = Assert.Single(OpenRecentList(window));
        Assert.Equal(FirstRun, ((TextBlock)earlier.Header!).Text);

        Click(earlier);
        await Eventually(() => GridRows(window) == 1);
        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpenPicker_ShowInFolder_AndExport_RunWithoutChangingTheRun()
    {
        var runs = NewRuns();
        var window = Open(runs, runs);
        var before = runs.Path;

        Click(Item(window, "OpenRunMenuItem"));           // picker returns no file
        Click(Item(window, "ShowRunInFolderMenuItem"));   // launches nothing
        Click(Assert.Single(Item(window, "ExportMenuItem").Items.Cast<MenuItem>())); // save picker returns none
        await Eventually(() => Directory.Exists(Path.GetDirectoryName(before)));

        Assert.Equal(before, runs.Path);
        Assert.False(Vm(window).HasNotice);
        Assert.False(File.Exists(before), "None of these commit anything.");
        window.Close();
    }

    [AvaloniaFact]
    public void OpenPicker_WithACohortPending_IsRefusedBeforeThePickerOpens_AndTheNoticeDismisses()
    {
        var runs = NewRuns();
        var window = Open(runs, runs);
        var vm = Vm(window);
        vm.LoadCohort(MakeCohort("Opt"));

        Click(Item(window, "OpenRunMenuItem"));
        Assert.True(vm.HasNotice);

        var dismiss = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Dismiss");
        Click(dismiss);
        Assert.False(vm.HasNotice);
        Assert.True(vm.HasPendingCohort);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RenameRun_ThroughTheDialog_RenamesOnRename_AndNotOnCancel()
    {
        var runs = NewRuns();
        var window = Open(runs, runs);

        RenameRunDialog Dialog()
        {
            Click(Item(window, "RenameRunMenuItem"));
            Dispatcher.UIThread.RunJobs();
            return Assert.IsType<RenameRunDialog>(Assert.Single(window.OwnedWindows));
        }

        Button DialogButton(RenameRunDialog dialog, string content) =>
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);

        var cancelled = Dialog();
        cancelled.GetVisualDescendants().OfType<TextBox>().Single().Text = "Ignored";
        Click(DialogButton(cancelled, "Cancel"));
        await Eventually(() => window.OwnedWindows.Count == 0);
        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);

        var renamed = Dialog();
        var box = renamed.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal(FirstRun, box.Text);
        box.Text = "Binder 3";
        Click(DialogButton(renamed, "Rename"));
        await Eventually(() => window.Title == "LoreFetch — Binder 3");

        Assert.Equal("Run: Binder 3 · 0 cards", Header(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningARunThatNoLongerExists_ShowsANotice()
    {
        var runs = NewRuns();
        var window = Open(runs, runs);

        await window.OpenRunAsync(Path.Combine(_root, "deleted.csv"));

        Assert.Equal("'deleted.csv' no longer exists.", Vm(window).NoticeMessage);
        Assert.Equal($"LoreFetch — {FirstRun}", window.Title);
        window.Close();
    }

    private sealed class SpyPipeline(Cohort? captureResult) : IScanPipeline
    {
#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<FrameSourceException>? SourceFailed;
        public event Action<Cohort>? AutoCaptured;
#pragma warning restore CS0067

        public string SourceDescription => "spy";

        public Task<Cohort?> CaptureAsync(CancellationToken ct) => Task.FromResult(captureResult);

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
