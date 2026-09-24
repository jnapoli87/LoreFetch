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
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// A9 tests — non-happy-path states: empty collection, SourceFailed banner,
/// store-lock retry, startup-error surface, and unresolved-tile affordance.
///
/// <para>Tier breakdown:</para>
/// <list type="bullet">
///   <item>Plain <c>[Fact]</c> tests cover VM-level behaviour with no Avalonia
///     infrastructure: <see cref="CollectionViewModel.IsEmpty"/> /
///     <see cref="CollectionViewModel.HasRows"/> changes,
///     <see cref="MainViewModel"/> error-message properties, and
///     <see cref="TileViewModel.ShowSetManuallyHint"/>.</item>
///   <item><c>[AvaloniaFact]</c> tests verify that the visual tree shows the
///     correct banners and affordances in the headless environment.</item>
/// </list>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class ErrorStateTests
{
    // -----------------------------------------------------------------------
    // 1. Empty collection — VM-level
    // -----------------------------------------------------------------------

    [Fact]
    public void CollectionViewModel_InitialState_IsEmpty()
    {
        var vm = new CollectionViewModel();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public async Task CollectionViewModel_AfterLoadWithRows_HasRows()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));

        var vm = new CollectionViewModel(store);
        await vm.LoadAsync(CancellationToken.None);

        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasRows);
    }

    [Fact]
    public async Task CollectionViewModel_IsEmpty_RaisesInpcOnChange()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));

        var vm = new CollectionViewModel(store);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Initially empty → load rows → IsEmpty and HasRows must be raised
        await vm.LoadAsync(CancellationToken.None);

        Assert.Contains(nameof(CollectionViewModel.IsEmpty), raised);
        Assert.Contains(nameof(CollectionViewModel.HasRows), raised);
    }

    // -----------------------------------------------------------------------
    // 2. Empty collection — visual tree
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void EmptyCollectionPlaceholder_VisibleWhenNoRows()
    {
        var session = MakeSession(store: new StubCollectionStore()); // no rows seeded
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var hint = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Name == "EmptyCollectionHint");

        Assert.NotNull(hint);
        Assert.True(hint.IsVisible, "EmptyCollectionHint must be visible when Rows is empty.");

        window.Close();
    }

    [AvaloniaFact]
    public async Task EmptyCollectionPlaceholder_HiddenWhenHasRows()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));

        var session = MakeSession(store: store);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Load the rows via the collection VM (same path as the Refresh button).
        // CollectionPanel.DataContext is a CollectionViewModel wired to the store.
        // Access it through the visual tree (the window's public surface).
        // The simplest way: find it in visual descendants via the DataGrid DataContext.
        var dataGrid = window.GetVisualDescendants()
            .OfType<DataGrid>()
            .FirstOrDefault(g => g.Name == "CollectionGrid");

        Assert.NotNull(dataGrid);
        if (dataGrid.DataContext is CollectionViewModel cvm)
        {
            await cvm.LoadAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
        }

        var hint = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Name == "EmptyCollectionHint");

        Assert.NotNull(hint);
        Assert.False(hint.IsVisible, "EmptyCollectionHint must be hidden when rows are loaded.");

        window.Close();
    }

    // -----------------------------------------------------------------------
    // 3. SourceFailed banner — visual tree + no stack trace
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task SourceFailed_Banner_VisibleWithMessage()
    {
        var pipeline = new FireableSourceFailedPipeline();
        var session = MakeSession(pipeline: pipeline);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Raise SourceFailed from a background thread — same as the real pipeline.
        await Task.Run(() => pipeline.RaiseSourceFailed("Device unplugged"));

        // Drain the Dispatcher.UIThread.Post() queued by OnSourceFailed.
        Dispatcher.UIThread.RunJobs();

        var banner = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "SourceErrorBanner");

        Assert.NotNull(banner);
        Assert.True(banner.IsVisible, "SourceErrorBanner must be visible after SourceFailed fires.");

        var text = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Name == "SourceErrorText");

        Assert.NotNull(text);
        Assert.Equal("Device unplugged", text.Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task SourceFailed_Banner_MessageContainsNoStackTrace()
    {
        var pipeline = new FireableSourceFailedPipeline();
        var session = MakeSession(pipeline: pipeline);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Use an exception with a real inner exception so ToString() would
        // contain "at " stack-trace lines, but Message should not.
        var inner = new InvalidOperationException("USB error");
        await Task.Run(() => pipeline.RaiseSourceFailedWithException(
            new FrameSourceException("Camera disconnected", inner)));

        Dispatcher.UIThread.RunJobs();

        var text = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Name == "SourceErrorText");

        Assert.NotNull(text);

        // Message must not contain stack-trace markers
        Assert.DoesNotContain(" at ", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text.Text, StringComparison.Ordinal);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // 4. Store-lock retry banner
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void StoreLock_Banner_VisibleAfterCommitFailure_AndCohortRetained()
    {
        var store = new StubCollectionStore();
        store.ArmNextCommitToThrow(); // simulates Excel locking the file
        var session = MakeSession(store: store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeCohort(2));
        Dispatcher.UIThread.RunJobs();

        // Press Enter — store throws → banner should appear, cohort retained
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Banner visible
        var banner = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "StoreLockBanner");

        Assert.NotNull(banner);
        Assert.True(banner.IsVisible, "StoreLockBanner must be visible after CollectionStoreException.");

        // Cohort still retained (A7 contract not regressed)
        Assert.Equal(2, vm.Tiles.Count);
        Assert.True(vm.HasPendingCohort);

        window.Close();
    }

    [AvaloniaFact]
    public void StoreLock_Retry_ClearsBannerOnSuccess()
    {
        var store = new OnceThrowingStore();
        var session = MakeSession(store: store);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeCohort(1));
        Dispatcher.UIThread.RunJobs();

        // First Enter: store throws → banner appears
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var banner = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "StoreLockBanner");

        Assert.NotNull(banner);
        Assert.True(banner.IsVisible, "StoreLockBanner must be visible after first (failing) Enter.");

        // Now click Retry — store succeeds → banner must disappear
        var retryButton = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "StoreLockRetryButton");

        Assert.NotNull(retryButton);
        retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(banner.IsVisible, "StoreLockBanner must be hidden after a successful Retry.");
        // Tile grid cleared after successful commit
        Assert.Empty(vm.Tiles);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // 5. Startup-error surface
    // -----------------------------------------------------------------------

    [Fact]
    public void StartupErrorMessage_SetOnVm_HasStartupErrorTrue()
    {
        var vm = new MainViewModel(new ScanSettings());

        Assert.False(vm.HasStartupError);
        Assert.Null(vm.StartupErrorMessage);

        vm.StartupErrorMessage = "Hash index file not found.";

        Assert.True(vm.HasStartupError);
        Assert.Equal("Hash index file not found.", vm.StartupErrorMessage);
    }

    [Fact]
    public void StartupErrorMessage_Set_RaisesInpc()
    {
        var vm = new MainViewModel(new ScanSettings());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.StartupErrorMessage = "Missing thresholds file.";

        Assert.Contains(nameof(MainViewModel.StartupErrorMessage), raised);
        Assert.Contains(nameof(MainViewModel.HasStartupError), raised);
    }

    [AvaloniaFact]
    public void StartupError_Banner_VisibleWithMessage()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Drive the startup-error state directly via the VM
        var vm = (MainViewModel)window.DataContext!;
        vm.StartupErrorMessage = "Hash index file not found at data/index.bin.";
        Dispatcher.UIThread.RunJobs();

        var banner = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "StartupErrorBanner");

        Assert.NotNull(banner);
        Assert.True(banner.IsVisible, "StartupErrorBanner must be visible when StartupErrorMessage is set.");

        var text = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Name == "StartupErrorText");

        Assert.NotNull(text);
        // Assert exact text — if a stack trace were appended the text would not match.
        Assert.Equal("Hash index file not found at data/index.bin.", text.Text);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // 6. Unresolved tile affordance
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Unresolved_ShowSetManuallyHint_True()
    {
        var tile = new CohortTile(
            MakeCard(),
            [new CardCandidate("oracle-x", "Unknown Card", 999, null)],
            goodDistance: 100, okDistance: 200);

        var vm = new TileViewModel(tile);

        // Unresolved tiles must expose the "Set manually" hint
        Assert.True(vm.IsUnresolved);
        Assert.True(vm.ShowSetManuallyHint);
    }

    [Fact]
    public void TileViewModel_Included_ShowSetManuallyHint_False()
    {
        var tile = new CohortTile(
            MakeCard(),
            [new CardCandidate("oracle-bolt", "Lightning Bolt", 50, null)],
            goodDistance: 100, okDistance: 200);

        var vm = new TileViewModel(tile);

        Assert.True(vm.IsIncluded);
        Assert.False(vm.ShowSetManuallyHint, "Only Unresolved tiles should show the hint.");
    }

    [AvaloniaFact]
    public void UnresolvedTile_VisualTree_ShowsSetManuallyHint()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        // Load a cohort with one Unresolved tile
        var unresolvedTile = new CohortTile(
            MakeCard(),
            [new CardCandidate("oracle-x", "Unknown Card", 999, null)],
            goodDistance: 100, okDistance: 200);
        var cohort = new Cohort(
            Guid.NewGuid(), DateTimeOffset.UtcNow, 1, CaptureReason.Manual, [unresolvedTile]);

        vm.LoadCohort(cohort);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The "SetManuallyHint" TextBlock must be visible in the visual tree
        // for the Unresolved tile
        var hints = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.Name == "SetManuallyHint" && tb.IsVisible)
            .ToList();

        Assert.NotEmpty(hints);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RectifiedCard MakeCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static Cohort MakeCohort(int count)
    {
        var card = MakeCard();
        var tiles = Enumerable.Range(0, count)
            .Select(i => new CohortTile(
                card,
                [new CardCandidate($"oracle-card{i}", $"Card {i}", 50, null)],
                goodDistance: 100,
                okDistance: 200))
            .ToList<CohortTile>();

        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, count, CaptureReason.Manual, tiles);
    }

    private static AppSession MakeSession(
        IScanPipeline? pipeline = null,
        ICollectionStore? store = null)
    {
        var settings = new ScanSettings();
        var p = pipeline ?? new NullPipeline();
        var source = new NullFrameSource();
        return new AppSession(p, source, Task.CompletedTask, settings, store: store);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pipeline that exposes a method to raise <see cref="SourceFailed"/>
    /// from a background thread, simulating a device failure.
    /// </summary>
    private sealed class FireableSourceFailedPipeline : IScanPipeline
    {
#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<Cohort>? AutoCaptured;
#pragma warning restore CS0067

        public event Action<FrameSourceException>? SourceFailed;

        public string SourceDescription => "fireable-test";

        public void RaiseSourceFailed(string message) =>
            SourceFailed?.Invoke(new FrameSourceException(message));

        public void RaiseSourceFailedWithException(FrameSourceException ex) =>
            SourceFailed?.Invoke(ex);

        public Task<Cohort?> CaptureAsync(CancellationToken ct) =>
            Task.FromResult<Cohort?>(null);

        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Store that throws <see cref="CollectionStoreException"/> on the first
    /// <see cref="CommitCohortAsync"/> call and succeeds on subsequent calls.
    /// Used to test the Retry banner flow end-to-end.
    /// </summary>
    private sealed class OnceThrowingStore : ICollectionStore
    {
        private readonly StubCollectionStore _inner = new();
        private int _commitAttempts;

        public Task<int> CommitCohortAsync(Cohort cohort, CancellationToken ct)
        {
            if (++_commitAttempts == 1)
                throw new CollectionStoreException("Simulated file lock on first attempt.");

            return _inner.CommitCohortAsync(cohort, ct);
        }

        public Task<IReadOnlyList<CollectionRow>> ListAsync(CancellationToken ct) =>
            _inner.ListAsync(ct);
    }

    private sealed class NullPipeline : IScanPipeline
    {
#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<Cohort>? AutoCaptured;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067

        public string SourceDescription => "null-test";

        public Task<Cohort?> CaptureAsync(CancellationToken ct) =>
            Task.FromResult<Cohort?>(null);

        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public string Description => "null-test";
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
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests"):
 *
 * Case 1 — Don't subscribe to SourceFailed (remove `_pipeline.SourceFailed += OnSourceFailed;`
 *           from MainWindow.axaml.cs):
 *   Mutation: removed the SourceFailed subscription line from the session ctor.
 *   Affected test: SourceFailed_Banner_VisibleWithMessage
 *   Result: FAILS — banner.IsVisible is false because OnSourceFailed is never
 *     called, so vm.SourceErrorMessage stays null and HasSourceError stays false.
 *     "Assert.True() Failure — Expected: True, Actual: False"
 *   Conclusion: fails for the right reason. ✓
 *
 * Case 2 — Don't show the StoreLock retry banner (remove the `Dispatcher.UIThread.Post`
 *           that sets vm.StoreLockMessage in OnEnterAsync's catch block):
 *   Mutation: removed the `vm.StoreLockMessage = msg;` line from the catch block,
 *     leaving the Post as a no-op lambda.
 *   Affected test: StoreLock_Banner_VisibleAfterCommitFailure_AndCohortRetained
 *   Result: FAILS — banner.IsVisible is false because StoreLockMessage stays null.
 *     "Assert.True() Failure — Expected: True, Actual: False"
 *   Conclusion: fails for the right reason. ✓
 *
 * Case 3 — Show the DataGrid even when Rows is empty (remove the EmptyCollectionHint
 *           TextBlock's IsVisible binding, or make it always visible):
 *   Mutation: changed `IsVisible="{Binding IsEmpty}"` to `IsVisible="True"` on
 *     EmptyCollectionHint in MainWindow.axaml.
 *   Affected test: EmptyCollectionPlaceholder_HiddenWhenHasRows
 *   Result: FAILS — hint.IsVisible is true even when rows exist.
 *     "Assert.False() Failure — Expected: False, Actual: True"
 *   Conclusion: fails for the right reason. ✓
 */
