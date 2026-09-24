using System.IO;
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
/// Tests for A8: collection DataGrid over <see cref="ICollectionStore.ListAsync"/>
/// and the generic export picker with <see cref="ExportFormat.IsVerified"/> badge.
///
/// <para>Tier breakdown:</para>
/// <list type="bullet">
///   <item>Plain <c>[Fact]</c> tests cover VM-level behaviour (no Avalonia
///     infrastructure needed): rows load, exporter items, export writes.</item>
///   <item><c>[AvaloniaFact]</c> tests verify the visual tree: the named
///     <c>CollectionGrid</c> DataGrid is present, and the "unverified" badge
///     border is visible for the unverified exporter.</item>
/// </list>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class CollectionViewTests
{
    // -----------------------------------------------------------------------
    // Test 1 — Rows load
    // -----------------------------------------------------------------------

    /// <summary>
    /// A <see cref="StubCollectionStore"/> seeded with known rows exposes those
    /// rows through <see cref="CollectionViewModel.Rows"/> after
    /// <see cref="CollectionViewModel.LoadAsync"/> is called.
    /// </summary>
    [Fact]
    public async Task RowsLoad_WithSeededStore_ExposesRows()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 2, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));
        store.Seed(new CollectionRow(
            "oracle-counter", "Counterspell", 1, null,
            DateTimeOffset.UtcNow, 80, RowSource.Hash, null));

        var vm = new CollectionViewModel(store, null);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains(vm.Rows, r => r.OracleName == "Lightning Bolt" && r.Quantity == 2);
        Assert.Contains(vm.Rows, r => r.OracleName == "Counterspell");
    }

    // -----------------------------------------------------------------------
    // Test 2 — Export picker lists all exporters with verified/unverified flag
    // -----------------------------------------------------------------------

    /// <summary>
    /// When one verified and one unverified exporter are registered, the VM
    /// exposes both through <see cref="CollectionViewModel.ExporterItems"/> and
    /// flags each correctly — the verified one has <c>IsVerified = true</c> and
    /// <c>IsUnverified = false</c>, and vice-versa for the unverified one.
    /// </summary>
    [Fact]
    public void ExportPickerItems_WithVerifiedAndUnverified_FlagsCorrectly()
    {
        var (verified, unverified) = MakeExporters();
        var vm = new CollectionViewModel(null, [verified, unverified]);

        Assert.Equal(2, vm.ExporterItems.Count);

        var verifiedItem = vm.ExporterItems.Single(e => e.IsVerified);
        var unverifiedItem = vm.ExporterItems.Single(e => !e.IsVerified);

        Assert.True(verifiedItem.IsVerified);
        Assert.False(verifiedItem.IsUnverified);

        Assert.False(unverifiedItem.IsVerified);
        Assert.True(unverifiedItem.IsUnverified);
    }

    // -----------------------------------------------------------------------
    // Test 3 — Export writes via the chosen exporter
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calling <see cref="CollectionViewModel.ExportToStreamAsync"/> with a
    /// seeded store and a <see cref="MemoryStream"/> results in the stream
    /// containing non-empty output that includes the seeded card name — proving
    /// the chosen exporter's <c>ExportAsync</c> was actually called with the
    /// rows.
    /// </summary>
    [Fact]
    public async Task ExportToStream_WritesViaChosenExporter()
    {
        var store = new StubCollectionStore();
        store.Seed(new CollectionRow(
            "oracle-bolt", "Lightning Bolt", 1, null,
            DateTimeOffset.UtcNow, 50, RowSource.Hash, null));

        var (verified, _) = MakeExporters();
        var vm = new CollectionViewModel(store, [verified]);

        using var ms = new MemoryStream();
        await vm.ExportToStreamAsync(verified, ms, CancellationToken.None);

        Assert.True(ms.Position > 0, "Exporter must have written bytes.");

        ms.Position = 0;
        var text = new StreamReader(ms).ReadToEnd();
        Assert.Contains("Lightning Bolt", text);
    }

    // -----------------------------------------------------------------------
    // Test 4 — Visual tree: CollectionGrid present + unverified badge visible
    // -----------------------------------------------------------------------

    /// <summary>
    /// After <c>Show()</c> and <c>RunJobs()</c>:
    /// <list type="bullet">
    ///   <item>A <see cref="DataGrid"/> named <c>CollectionGrid</c> is in the
    ///     visual tree.</item>
    ///   <item>The "unverified" badge <see cref="Border"/> for the unverified
    ///     exporter is visible (its own <c>IsVisible</c> is true — the badge
    ///     is a Border bound to <c>IsUnverified</c>).</item>
    /// </list>
    /// </summary>
    [AvaloniaFact]
    public void VisualTree_CollectionGrid_PresentAndUnverifiedBadgeVisible()
    {
        var (verified, unverified) = MakeExporters();
        var store = new StubCollectionStore();
        var session = MakeSession(store, [verified, unverified]);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 1. CollectionGrid DataGrid must be in the visual tree.
        var dataGrid = window.GetVisualDescendants()
            .OfType<DataGrid>()
            .FirstOrDefault(g => g.Name == "CollectionGrid");
        Assert.NotNull(dataGrid);

        // 2. The unverified badge Border must be visible. The badge is a Border
        // with IsVisible="{Binding IsUnverified}" and a TextBlock child whose
        // text is "unverified". Only the unverified exporter's row has it visible.
        // Search for Borders where IsVisible=true and the child TextBlock says "unverified".
        var visibleBadges = window.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.IsVisible && b.Child is TextBlock { Text: "unverified" })
            .ToList();

        Assert.NotEmpty(visibleBadges);

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (StubCollectionExporter Verified, StubCollectionExporter Unverified)
        MakeExporters() =>
        (
            new StubCollectionExporter(new ExportFormat(
                "test-verified",
                "Test Verified Export",
                ".txt",
                IsVerified: true,
                Notes: null)),
            new StubCollectionExporter(new ExportFormat(
                "test-unverified",
                "Test Unverified Export",
                ".txt",
                IsVerified: false,
                Notes: "Not yet tested with a live tool."))
        );

    private static AppSession MakeSession(
        ICollectionStore? store = null,
        IReadOnlyList<ICollectionExporter>? exporters = null)
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        return new AppSession(pipeline, source, Task.CompletedTask, settings,
            store: store, exporters: exporters);
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
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests"):
 *
 * Case 1 — Make the picker always treat exporters as verified
 *   (return `false` from ExporterItem.IsUnverified, or equivalently `true` from IsVerified):
 *   Mutation applied: changed `ExporterItem.IsUnverified` to `return false` unconditionally.
 *   Affected test: ExportPickerItems_WithVerifiedAndUnverified_FlagsCorrectly
 *   Result: FAILS — Assert.True(unverifiedItem.IsUnverified) fails because
 *     IsUnverified returns false instead of true.
 *     "Assert.True() Failure — Expected: True, Actual: False"
 *   Conclusion: fails for the right reason. ✓
 *
 * Case 2 — Make the export method ignore the passed exporter / not call ExportAsync:
 *   Mutation applied: removed `await exporter.ExportAsync(rows, destination, ct)` from
 *     CollectionViewModel.ExportToStreamAsync (left rows fetch, skipped the write).
 *   Affected test: ExportToStream_WritesViaChosenExporter
 *   Result: FAILS — Assert.True(ms.Position > 0, "Exporter must have written bytes.")
 *     fails because the stream was never written to (Position remains 0).
 *   Conclusion: fails for the right reason. ✓
 *
 * Case 3 — Bind the DataGrid to an empty list instead of ListAsync results:
 *   Mutation applied: replaced the `foreach` body in CollectionViewModel.LoadAsync
 *     with nothing (called `Rows.Clear()` only, no subsequent `Rows.Add()`).
 *   Affected test: RowsLoad_WithSeededStore_ExposesRows
 *   Result: FAILS — Assert.Equal(2, vm.Rows.Count) fails because Rows is empty.
 *     "Assert.Equal() Failure — Expected: 2, Actual: 0"
 *   Conclusion: fails for the right reason. ✓
 */
