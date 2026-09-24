using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// Tests for A6: tile mouse interactions (<see cref="TileViewModel.ToggleExcludedFromUi"/>,
/// <see cref="TileViewModel.SetManuallyFromUi"/>, <see cref="TileViewModel.ClearFromUi"/>)
/// and the "Set card manually…" type-ahead populator
/// (<see cref="TileViewModel.TypeAheadPopulator"/>).
///
/// <para>
/// Interaction unit tests are plain xUnit facts — no Avalonia host needed.
/// The visual-tree test uses <c>[AvaloniaFact]</c> and is cross-platform.
/// </para>
///
/// <para>
/// Chaos test results are documented at the bottom of this file.
/// </para>
/// </summary>
public class TileInteractionTests
{
    // -----------------------------------------------------------------------
    // Shared constants and helpers
    // -----------------------------------------------------------------------

    private const int Good = 100;
    private const int Ok = 200;

    private static RectifiedCard MakeTestCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    private static CohortTile MakeIncludedTile(string name = "Lightning Bolt") =>
        new(MakeTestCard(), [Candidate(name, 50)], Good, Ok);

    private static CohortTile MakeUnresolvedTile() =>
        new(MakeTestCard(), [Candidate("Mystery", 500)], Good, Ok);

    // -----------------------------------------------------------------------
    // Interaction unit tests: ToggleExcludedFromUi
    // -----------------------------------------------------------------------

    [Fact]
    public void ToggleExcludedFromUi_TogglesStateToExcluded_AndRaisesInpc()
    {
        var tile = MakeIncludedTile();
        var vm = new TileViewModel(tile);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ToggleExcludedFromUi();

        Assert.Equal(TileState.Excluded, vm.State);
        Assert.True(vm.IsExcluded);
        Assert.True(vm.ShowExcludedMarker);
        Assert.Contains(nameof(TileViewModel.State), raised);
        Assert.Contains(nameof(TileViewModel.IsExcluded), raised);
    }

    [Fact]
    public void ToggleExcludedFromUi_TogglesBackToIncluded_AndRaisesInpc()
    {
        var tile = MakeIncludedTile();
        var vm = new TileViewModel(tile);

        vm.ToggleExcludedFromUi(); // Included → Excluded
        Assert.Equal(TileState.Excluded, vm.State);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ToggleExcludedFromUi(); // Excluded → back to Included
        Assert.Equal(TileState.Included, vm.State);
        Assert.False(vm.IsExcluded);
        Assert.False(vm.ShowExcludedMarker);
        Assert.Contains(nameof(TileViewModel.State), raised);
    }

    [Fact]
    public void ToggleExcludedFromUi_OptOutDefaultIsIncluded()
    {
        // The opt-out default must be Included — never flipped to opt-in
        // (reviewer point 6). A fresh tile should be Included.
        var tile = MakeIncludedTile();
        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Included, vm.State);
    }

    // -----------------------------------------------------------------------
    // Interaction unit tests: SetManuallyFromUi
    // -----------------------------------------------------------------------

    [Fact]
    public void SetManuallyFromUi_SetsStateToManuallySet_AndRaisesInpc()
    {
        var tile = MakeUnresolvedTile(); // start from Unresolved to test correction
        var vm = new TileViewModel(tile);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var entry = new OracleEntry("oracle-lotus", "Black Lotus");
        vm.SetManuallyFromUi(entry);

        Assert.Equal(TileState.ManuallySet, vm.State);
        Assert.True(vm.IsManuallySet);
        Assert.Equal("Black Lotus", vm.DisplayName);
        Assert.Equal(string.Empty, vm.DistanceText); // SetManually clears distance
        Assert.False(vm.IsLowConfidence);
        Assert.Contains(nameof(TileViewModel.State), raised);
        Assert.Contains(nameof(TileViewModel.IsManuallySet), raised);
        Assert.Contains(nameof(TileViewModel.DisplayName), raised);
    }

    [Fact]
    public void SetManuallyFromUi_CanCorrectAnIncludedTile()
    {
        var tile = MakeIncludedTile("Wrong Guess");
        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Included, vm.State);
        Assert.Equal("Wrong Guess", vm.DisplayName);

        vm.SetManuallyFromUi(new OracleEntry("oracle-bolt", "Lightning Bolt"));

        Assert.Equal(TileState.ManuallySet, vm.State);
        Assert.Equal("Lightning Bolt", vm.DisplayName);
    }

    // -----------------------------------------------------------------------
    // Interaction unit tests: ClearFromUi
    // -----------------------------------------------------------------------

    [Fact]
    public void ClearFromUi_RevertsManualSetToIncluded_AndRaisesInpc()
    {
        var tile = MakeIncludedTile("Lightning Bolt");
        var vm = new TileViewModel(tile);
        vm.SetManuallyFromUi(new OracleEntry("oracle-bolt", "Lightning Bolt"));
        Assert.Equal(TileState.ManuallySet, vm.State);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ClearFromUi();

        // Distance ≤ Good → reverts to Included
        Assert.Equal(TileState.Included, vm.State);
        Assert.True(vm.IsIncluded);
        Assert.Contains(nameof(TileViewModel.State), raised);
    }

    [Fact]
    public void ClearFromUi_RevertsManualSetToUnresolved_WhenDistanceIsHigh()
    {
        var tile = MakeUnresolvedTile(); // distance=500 > Ok
        var vm = new TileViewModel(tile);

        vm.SetManuallyFromUi(new OracleEntry("oracle-x", "Black Lotus"));
        Assert.Equal(TileState.ManuallySet, vm.State);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ClearFromUi();

        Assert.Equal(TileState.Unresolved, vm.State);
        Assert.True(vm.IsUnresolved);
        Assert.Contains(nameof(TileViewModel.State), raised);
    }

    [Fact]
    public void ClearFromUi_IsNoOpOnNonManualTile()
    {
        var tile = MakeIncludedTile();
        var vm = new TileViewModel(tile);

        // CohortTile.Clear() is a no-op unless State == ManuallySet
        vm.ClearFromUi();

        Assert.Equal(TileState.Included, vm.State);
    }

    // -----------------------------------------------------------------------
    // Type-ahead populator tests
    // -----------------------------------------------------------------------

    // Helper: call the populator synchronously (it's async but we can await).
    private static async Task<IReadOnlyList<CatalogItem>> CallPopulator(
        Func<string?, CancellationToken, Task<IEnumerable<object>>> populator,
        string? prefix,
        CancellationToken ct = default)
    {
        var results = await populator(prefix, ct);
        return results.OfType<CatalogItem>().ToList();
    }

    [Fact]
    public async Task Populator_RunnersUpAppearFirst()
    {
        // Build a tile whose Candidates list contains a specific name.
        var tile = new CohortTile(
            MakeTestCard(),
            [
                Candidate("Lightning Bolt", 50),   // index 0 — the runner-up to check
                Candidate("Counterspell", 80),      // index 1
            ],
            Good, Ok);

        // Catalog has a "Lightning" entry too — it must appear AFTER the runner-up.
        var catalog = new StubOracleCatalog(entryCount: 0); // 5 hostile names only
        // Manually augment: use a small catalog that also has a "Lightning" card.
        // StubOracleCatalog hostile names don't start with "Li", so a 5-entry stub
        // won't interfere. We construct a small real catalog via InlineOracleCatalog.
        var catalogWithLightning = new InlineOracleCatalog([
            new OracleEntry("catalog-li", "Lightning Strike"), // catalog entry starting with "Li"
        ]);

        var vm = new TileViewModel(tile, catalogWithLightning);

        var results = await CallPopulator(vm.TypeAheadPopulator, "Li", TestContext.Current.CancellationToken);

        // Both entries start with "Li".
        // The runner-up (oracle-Lightning Bolt) must appear BEFORE the catalog entry.
        Assert.True(results.Count >= 2, "Expected at least two results");
        Assert.Equal("Lightning Bolt", results[0].OracleName);
    }

    [Fact]
    public async Task Populator_CapsResultsAt20_EvenWith33kCatalog()
    {
        // StubOracleCatalog has 33,000 entries named "Synthetic Card N" (N=5..32999)
        // plus 5 hostile names. Prefix "Synthetic" matches all synthetic entries.
        var tile = new CohortTile(MakeTestCard(), [Candidate("Other", 50)], Good, Ok);
        var catalog = new StubOracleCatalog(); // 33,000 entries

        var vm = new TileViewModel(tile, catalog);
        var results = await CallPopulator(vm.TypeAheadPopulator, "Synthetic", TestContext.Current.CancellationToken);

        Assert.True(results.Count <= 20, $"Expected ≤20 results, got {results.Count}");
    }

    [Fact]
    public async Task Populator_StartsWithOrdinal_SubstringOnlyMatchIsExcluded()
    {
        // "Bolt" is a substring of "Lightning Bolt" but does NOT start with "Bolt".
        // StartsWithOrdinal means only entries whose name starts with the prefix
        // are returned.
        var tile = new CohortTile(MakeTestCard(), [Candidate("Lightning Bolt", 50)], Good, Ok);
        var catalog = new InlineOracleCatalog([
            new OracleEntry("oracle-bolt", "Bolt"),     // starts with "Bolt" ✓
            new OracleEntry("oracle-lb", "Lightning Bolt"), // does NOT start with "Bolt"
        ]);

        var vm = new TileViewModel(tile, catalog);
        var results = await CallPopulator(vm.TypeAheadPopulator, "Bolt", TestContext.Current.CancellationToken);

        // Only "Bolt" should appear; "Lightning Bolt" contains "Bolt" but doesn't start with it.
        Assert.All(results, r => Assert.StartsWith("Bolt", r.OracleName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Populator_HostileName_PlusSignPrefix_IsFoundable()
    {
        // "+2 Mace" starts with "+2". StartsWithOrdinal with prefix "+2" must find it.
        var tile = new CohortTile(MakeTestCard(), [Candidate("Other", 50)], Good, Ok);
        var catalog = new StubOracleCatalog(); // includes "+2 Mace"

        var vm = new TileViewModel(tile, catalog);
        var results = await CallPopulator(vm.TypeAheadPopulator, "+2", TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.OracleName == "+2 Mace");
    }

    [Fact]
    public async Task Populator_CancelledToken_ThrowsOrReturnsEmpty()
    {
        var tile = new CohortTile(MakeTestCard(), [Candidate("Other", 50)], Good, Ok);
        var catalog = new StubOracleCatalog();
        var vm = new TileViewModel(tile, catalog);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        // The populator may either throw OperationCanceledException or return empty.
        // Both are acceptable designs; the test just ensures no result leaks through.
        IReadOnlyList<CatalogItem> results;
        try
        {
            results = await CallPopulator(vm.TypeAheadPopulator, "Synthetic", cts.Token);
            // If no exception: results must be empty (cancellation observed without throw)
            Assert.Empty(results);
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation propagated correctly
        }
    }

    [Fact]
    public async Task Populator_NullCatalog_ReturnsRunnersUpOnly()
    {
        // When no catalog is supplied, the populator still returns Candidates matches.
        var tile = new CohortTile(
            MakeTestCard(),
            [Candidate("Lightning Bolt", 50)],
            Good, Ok);

        var vm = new TileViewModel(tile, catalog: null);
        var results = await CallPopulator(vm.TypeAheadPopulator, "Li", TestContext.Current.CancellationToken);

        // Runner-up "Lightning Bolt" starts with "Li" → should appear.
        Assert.Contains(results, r => r.OracleName == "Lightning Bolt");
    }

    [Fact]
    public async Task Populator_ShortPrefix_ReturnsEmpty()
    {
        var tile = MakeIncludedTile();
        var catalog = new StubOracleCatalog();
        var vm = new TileViewModel(tile, catalog);

        // Prefix shorter than 2 chars → empty, regardless of catalog size.
        var results1 = await CallPopulator(vm.TypeAheadPopulator, "L", TestContext.Current.CancellationToken);
        var results0 = await CallPopulator(vm.TypeAheadPopulator, "", TestContext.Current.CancellationToken);
        var resultsNull = await CallPopulator(vm.TypeAheadPopulator, null, TestContext.Current.CancellationToken);

        Assert.Empty(results1);
        Assert.Empty(results0);
        Assert.Empty(resultsNull);
    }

    [Fact]
    public async Task Populator_DeduplicatesCandidatesAndCatalog()
    {
        // If a Candidate's OracleId also appears in the catalog, it should only
        // appear once (runners-up win, catalog entry is suppressed).
        var tile = new CohortTile(
            MakeTestCard(),
            [Candidate("Lightning Bolt", 50)], // OracleId = "oracle-Lightning Bolt"
            Good, Ok);

        var catalog = new InlineOracleCatalog([
            // Same OracleId as the Candidate — should be deduplicated.
            new OracleEntry("oracle-Lightning Bolt", "Lightning Bolt"),
            new OracleEntry("oracle-lb2", "Lightning Strike"),
        ]);

        var vm = new TileViewModel(tile, catalog);
        var results = await CallPopulator(vm.TypeAheadPopulator, "Li", TestContext.Current.CancellationToken);

        // "Lightning Bolt" should appear exactly once.
        var bolts = results.Where(r => r.OracleName == "Lightning Bolt").ToList();
        Assert.Single(bolts);
    }

    // -----------------------------------------------------------------------
    // Visual-tree test: ContextMenu with two items
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void TileContextMenu_CarriesTwoItems_SetManuallyAndClear()
    {
        var session = MakeSession();
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Find the tile's outer Grid (Width=80, Height=141) — the one that
        // the DataTemplate produces. WrapPanel children are Grids.
        var tileGrid = window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.Width == 80 && g.Height == 141);

        Assert.NotNull(tileGrid);

        var contextMenu = tileGrid.ContextMenu;
        Assert.NotNull(contextMenu);

        // Exactly two items: "Set card manually…" and "Clear".
        Assert.Equal(2, contextMenu.Items.Count);

        var headers = contextMenu.Items
            .OfType<MenuItem>()
            .Select(mi => mi.Header?.ToString())
            .ToList();

        Assert.Contains("Set card manually…", headers);
        Assert.Contains("Clear", headers);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Helpers for visual-tree tests (mirrors CohortGridTests)
    // -----------------------------------------------------------------------

    private static Cohort MakeSingleCohort()
    {
        var card = MakeTestCard();
        var tile = new CohortTile(card, [Candidate("Lightning Bolt", 50)], Good, Ok);
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, CaptureReason.Manual, [tile]);
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

    /// <summary>
    /// Minimal in-memory <see cref="IOracleCatalog"/> for tests that need
    /// specific entries rather than the full 33k stub.
    /// </summary>
    private sealed class InlineOracleCatalog : IOracleCatalog
    {
        public InlineOracleCatalog(IReadOnlyList<OracleEntry> entries)
        {
            All = entries;
        }

        public IReadOnlyList<OracleEntry> All { get; }
    }
}

/*
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests"):
 *
 * Case 1 — Refresh() dropped from ToggleExcludedFromUi:
 *   Affected test: ToggleExcludedFromUi_TogglesStateToExcluded_AndRaisesInpc
 *   Mutation: removed the `Refresh()` call inside ToggleExcludedFromUi.
 *   Result: FAILS — `raised` is empty, so `Assert.Contains("State", raised)` throws
 *     xUnit.net.Assert.Contains failure: "State" not found in [].
 *   Conclusion: the test fails for the right reason (no INPC fired). ✓
 *
 * Case 2 — .Take(20) removed from populator:
 *   Affected test: Populator_CapsResultsAt20_EvenWith33kCatalog
 *   Mutation: removed `if (results.Count >= 20) return results;` from the
 *     catalog-scan loop in BuildPopulator.
 *   Result: FAILS — results.Count is 33,000-range, assertion
 *     `Assert.True(results.Count <= 20)` fails with a count in the thousands.
 *   Conclusion: the test fails for the right reason (uncapped scan). ✓
 *
 * Case 3 — Runners-up-first logic removed:
 *   Affected test: Populator_RunnersUpAppearFirst
 *   Mutation: removed the Candidates loop from BuildPopulator (catalog-only scan).
 *   Result: FAILS — with catalog-only scanning, "Lightning Bolt" (the runner-up) is no
 *     longer in the results at all. The catalog only has "Lightning Strike", so results
 *     has 1 entry, and `Assert.True(results.Count >= 2, "Expected at least two results")`
 *     fails. The root cause is the same: the runners-up loop is missing, so runner-up
 *     entries vanish from the output entirely.
 *   Conclusion: the test fails for the right reason (runners-up absent). ✓
 */
