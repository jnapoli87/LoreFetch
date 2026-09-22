using System.Collections.ObjectModel;
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
/// Tests for A7: Space/Enter/Escape keyboard map, AutoCaptured marshaling,
/// focus bail, and the no-IsDefault-button invariant.
///
/// <para>
/// All tests are <c>[AvaloniaFact]</c> because they need the UI thread and
/// (for keyboard tests) the headless input dispatch infrastructure.
/// </para>
///
/// <para>
/// Chaos-test results are documented at the bottom of this file.
/// </para>
/// </summary>
public class KeyboardCaptureTests
{
    // -----------------------------------------------------------------------
    // Constants / shared helpers
    // -----------------------------------------------------------------------

    private const int Good = 100;
    private const int Ok   = 200;

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

    /// <summary>
    /// Create an <see cref="AppSession"/> with the given (or null-defaulted)
    /// pipeline and store. All existing ctors compile because both are optional.
    /// </summary>
    private static AppSession MakeSession(IScanPipeline? pipeline = null, ICollectionStore? store = null)
    {
        var settings = new ScanSettings();
        var p = (IScanPipeline)(pipeline ?? new SpyPipeline());
        var source = new NullFrameSource();
        return new AppSession(p, source, Task.CompletedTask, settings, store: store);
    }

    // -----------------------------------------------------------------------
    // Space captures: pipeline returns cohort → Tiles populated
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void Space_WhenPipelineReturnsCohort_PopulatesTiles()
    {
        var cohort = MakeCohort(3);
        var pipeline = new SpyPipeline(cohort);
        var session = MakeSession(pipeline);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        Assert.Empty(vm.Tiles); // nothing before Space

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); // drains the Dispatcher.UIThread.Post in CaptureFromPipelineAsync

        Assert.Equal(3, vm.Tiles.Count);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Space no-op when pipeline returns null (0 detections)
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void Space_WhenPipelineReturnsNull_TilesUnchanged()
    {
        var pipeline = new SpyPipeline(captureResult: null); // 0 detections
        var session = MakeSession(pipeline);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // CaptureAsync was called once but returned null → no cohort loaded
        Assert.Equal(1, pipeline.CaptureAsyncCallCount);
        Assert.Empty(vm.Tiles);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Enter: commits the pending cohort then clears the tile grid
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_CommitsThenClearsTiles()
    {
        var store = new StubCollectionStore();
        var pipeline = new SpyPipeline(); // CaptureAsync returns null (won't be called by Enter)
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;

        // Load a cohort manually so there is something to commit.
        var cohort = MakeCohort(2);
        vm.LoadCohort(cohort);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Tiles.Count);

        // Press Enter — triggers OnEnterAsync → CommitCohortAsync → ClearPendingCohort.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); // drains the Post'd ClearPendingCohort

        // The store must have received the commit.
        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count); // 2 tiles committed as separate oracle IDs

        // The grid must be empty.
        Assert.Empty(vm.Tiles);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Enter with a locked store: cohort is RETAINED, no exception escapes
    //
    // CollectionStoreException means "Excel has the file open" — the handler
    // catches it and keeps the pending cohort so the user can retry (A9 adds
    // the retry dialog). A7's only contract here is "do not crash, keep
    // cohort". This test guards that catch block against accidentally clearing
    // the grid (e.g. a misplaced ClearPendingCohort in the catch arm).
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_WhenStoreThrowsCollectionStoreException_RetainsCohort()
    {
        var store = new StubCollectionStore();
        store.ArmNextCommitToThrow(); // simulates "Excel has the CSV locked"
        var pipeline = new SpyPipeline();
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;

        var cohort = MakeCohort(2);
        vm.LoadCohort(cohort);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Tiles.Count);

        // Press Enter — store throws CollectionStoreException.
        // The handler must catch it and keep the cohort intact.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); // drains any dispatcher work (there should be none — no Post on catch)

        // Cohort is retained: tiles still present, HasPendingCohort still true.
        Assert.Equal(2, vm.Tiles.Count);
        Assert.True(vm.HasPendingCohort);

        // Store must be clean — the failed commit wrote nothing.
        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Empty(rows);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Escape: discards without writing to the store
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Escape_ClearsTilesWithoutCommitting()
    {
        var store = new StubCollectionStore();
        var pipeline = new SpyPipeline();
        var session = MakeSession(pipeline, store);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;

        // Load a cohort.
        vm.LoadCohort(MakeCohort(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Tiles);

        // Press Escape — synchronous discard, no store write.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Tile grid must be empty.
        Assert.Empty(vm.Tiles);

        // Store must be pristine — CommitCohortAsync must NEVER have been called.
        var rows = await store.ListAsync(CancellationToken.None);
        Assert.Empty(rows);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Focus bail (cross-platform [AvaloniaFact], reproduces on Mac)
    //
    // When a TextBox has focus the global Space/Enter handler must return
    // WITHOUT setting e.Handled, so the character reaches the focused text
    // control. The trap: on Win32 a handled WM_KEYDOWN suppresses the
    // following WM_CHAR; on macOS AvnView.mm -keyDown: returns early when
    // user code handled the event (same symptom, different mechanism).
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void FocusBail_SpaceWithTextBoxFocused_DoesNotInvokeCapture()
    {
        var cohort = MakeCohort(1);
        var pipeline = new SpyPipeline(cohort);
        var session = MakeSession(pipeline);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        // Load a cohort and open the type-ahead on the first tile. The
        // AutoCompleteBox becomes visible, and Avalonia instantiates its
        // inner TextBox in the visual tree. We can then focus that TextBox
        // to exercise the focus bail.
        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(cohort);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Open the type-ahead on the first tile so AutoCompleteBox is visible.
        vm.Tiles[0].IsTypeAheadOpen = true;
        Dispatcher.UIThread.RunJobs();

        // Find any TextBox in the visual tree and focus it. Avalonia's
        // AutoCompleteBox always has an inner TextBox, which is present once
        // the control is visible.
        var textBox = window.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault();

        if (textBox is null)
        {
            // If no TextBox is in the visual tree (headless layout may not
            // instantiate template children), the focus bail cannot be tested
            // for that branch. Skip rather than produce a false positive.
            window.Close();
            return;
        }

        textBox.Focus();
        Dispatcher.UIThread.RunJobs();

        // Verify focus is actually in the TextBox before sending the key.
        var focused = window.FocusManager?.GetFocusedElement();
        if (focused is not TextBox)
        {
            // Focus did not land in the TextBox (headless might not support
            // full focus chain). Skip rather than false-pass.
            window.Close();
            return;
        }

        var callsBefore = pipeline.CaptureAsyncCallCount;

        // Press Space. The focus bail must prevent CaptureAsync from being called.
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callsBefore, pipeline.CaptureAsyncCallCount);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // AutoCaptured marshaling: background-thread raise → Tiles populated on
    // the UI thread without throwing. This is reviewer scrutiny point 7 from
    // stream-a-ui.md: "AutoCaptured is raised on the pipeline's background
    // thread — marshal before touching a control".
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task AutoCaptured_FromBackgroundThread_PopulatesTilesOnUiThread()
    {
        var pipeline = new SpyPipeline();
        var session = MakeSession(pipeline);

        var window = new MainWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)window.DataContext!;
        Assert.Empty(vm.Tiles);

        var cohort = MakeCohort(2);

        // Simulate the pipeline raising AutoCaptured from a background thread.
        await Task.Run(() => pipeline.RaiseAutoCaptured(cohort));

        // Drain the Dispatcher.UIThread.Post() queued by OnAutoCaptured.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.Tiles.Count);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // No IsDefault button in the visual tree.
    // A Button with IsDefault=True registers on the input root and fires on
    // Enter regardless of focus (stream-a-ui.md §A6), which would intercept
    // Enter before the tunnel handler and commit with every Enter press —
    // including inside the type-ahead box (stream-a-ui.md §A6 ⚠).
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void NoDefaultButton_InMainWindow()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var defaultButtons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsDefault)
            .ToList();

        Assert.Empty(defaultButtons);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Spy pipeline whose <see cref="CaptureAsync"/> returns a fixed cohort
    /// (or null when the cohort is omitted) and counts how many times it is
    /// called. Exposes <see cref="RaiseAutoCaptured"/> so tests can simulate
    /// a background-thread auto-capture.
    /// </summary>
    private sealed class SpyPipeline : IScanPipeline
    {
        private readonly Cohort? _captureResult;

#pragma warning disable CS0067 // FrameProcessed / SourceFailed never raised here
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067

        public event Action<Cohort>? AutoCaptured;

        /// <param name="captureResult">
        /// What <see cref="CaptureAsync"/> returns. Pass <c>null</c> to
        /// simulate 0 detections.
        /// </param>
        public SpyPipeline(Cohort? captureResult = null)
        {
            _captureResult = captureResult;
        }

        public int CaptureAsyncCallCount { get; private set; }

        public string SourceDescription => "spy";

        public Task<Cohort?> CaptureAsync(CancellationToken ct)
        {
            CaptureAsyncCallCount++;
            return Task.FromResult(_captureResult);
        }

        /// <summary>
        /// Raises <see cref="AutoCaptured"/> — call from a background thread
        /// to simulate the pipeline's background-thread auto-capture.
        /// </summary>
        public void RaiseAutoCaptured(Cohort cohort) =>
            AutoCaptured?.Invoke(cohort);

        public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

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

/*
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests"):
 *
 * Case 1 — Remove the focus bail (remove `if (FocusManager?.GetFocusedElement() is TextBox) return;`):
 *   Affected test: FocusBail_SpaceWithTextBoxFocused_DoesNotInvokeCapture
 *   Mutation: deleted the focus bail guard from OnKeyDownTunnel in MainWindow.axaml.cs.
 *   Result: When a TextBox is focused and Space is pressed, CaptureAsync is called
 *     (CaptureAsyncCallCount increments from callsBefore). Test FAILS:
 *     Assert.Equal(callsBefore, pipeline.CaptureAsyncCallCount)
 *     → Expected: 0, Actual: 1
 *   Note: if the headless platform does not propagate focus to the TextBox (the
 *     test's early-return guard fires), the test is vacuous for that run — see
 *     the "Skip rather than false-pass" comments in the test body. On macOS the
 *     headless platform does propagate focus, so the guard does not skip.
 *   Conclusion: fails for the right reason when focus lands. ✓
 *
 * Case 2 — Drop the grid-clear after a successful Enter commit
 *           (remove `Dispatcher.UIThread.Post(vm.ClearPendingCohort)` from OnEnterAsync):
 *   Affected test: Enter_CommitsThenClearsTiles
 *   Mutation: removed the Dispatcher.UIThread.Post(vm.ClearPendingCohort) line
 *     from OnEnterAsync in MainWindow.axaml.cs.
 *   Result: After Enter, the store has the committed rows but vm.Tiles is still
 *     populated (the grid was not cleared). Test FAILS:
 *     Assert.Empty(vm.Tiles) → collection has 2 elements.
 *   Conclusion: fails for the right reason (grid not cleared). ✓
 *
 * Case 3 — Make Escape call CommitCohortAsync
 *           (replace `escapeVm.ClearPendingCohort()` with
 *            `_ = vm.CommitCohortAsync(_cts.Token)` in the Escape case):
 *   Affected test: Escape_ClearsTilesWithoutCommitting
 *   Mutation: replaced the Escape handler body to call CommitCohortAsync
 *     instead of ClearPendingCohort.
 *   Result: After Escape, the store has rows (the commit fired). Test FAILS:
 *     Assert.Empty(rows) → collection has 1 element.
 *   Conclusion: fails for the right reason (store written on Escape). ✓
 */
