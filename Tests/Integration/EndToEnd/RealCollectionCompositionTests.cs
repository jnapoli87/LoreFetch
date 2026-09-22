using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using LoreFetch.Core.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Package I1: the collection store and both export adapters are real now
/// (`LoreFetch.Core.Collection.CsvCollectionStore`,
/// `LoreFetch.Core.Export.NativeCsvExporter`,
/// `LoreFetch.Core.Export.MoxfieldCsvExporter`). Unlike `EndToEndTests`,
/// which needs the whole pipeline (detector/rectifier/identifier/frame
/// source — still I2/I3's) and therefore still calls `EnsureAvailable` and
/// still skips under `kind: Real`, every case here needs ONLY the store
/// and/or the exporters, so it calls `EnsureCollectionAvailable` and — for
/// `Real`, as of this package — actually runs against the real classes
/// instead of skipping.
public class RealCollectionCompositionTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    /// The store/exporter-only counterpart to `EndToEndTests`: no pipeline
    /// involved at all, so `Fakes` and `Real` both actually run this case —
    /// proving `IImplementationSet.CreateExporters()` for `Real` returns
    /// adapters that can actually export what `CreateCollectionStore()`
    /// just committed, through the real `ICollectionStore`/`ICollectionExporter`
    /// seam rather than a hand-wired shortcut.
    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task CommittedRows_ExportThroughEveryRegisteredExporter(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureCollectionAvailable();

        var ct = TestContext.Current.CancellationToken;
        var store = set.CreateCollectionStore(NewCollectionStorePath());
        var cohort = SingleTileCohort("oracle-forest", "Forest", distance: GoodDistance - 10);

        var cardsCommitted = await store.CommitCohortAsync(cohort, ct);
        Assert.Equal(1, cardsCommitted);

        var rows = await store.ListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal("Forest", row.OracleName);

        var exporters = set.CreateExporters();
        Assert.NotEmpty(exporters);

        foreach (var exporter in exporters)
        {
            using var destination = new MemoryStream();
            await exporter.ExportAsync(rows, destination, ct);

            Assert.True(destination.Length > 0, $"{exporter.Format.Id} wrote an empty file.");

            var text = System.Text.Encoding.UTF8.GetString(destination.ToArray());
            Assert.Contains("Forest", text, StringComparison.Ordinal);
        }
    }

    /// The Windows-only integration-level proof of I1's locked-file path
    /// (D's own `CsvCollectionStoreWriteSequenceTests` proves the store in
    /// isolation and is itself `WindowsOnly` — this proves the SAME
    /// behaviour reached through `AppComposition.ComposeAsync`, the actual
    /// composition path this package wires `Real` mode through, on the
    /// actual ship platform). Holds `collection.csv` open with
    /// `FileShare.ReadWrite` (no `FileShare.Delete`) — the "Excel has it
    /// open" case `CsvCollectionStoreWriteSequenceTests` documents as the
    /// realistic one, and the one that reaches the write path rather than
    /// failing at the read step — commits through it, asserts
    /// `CollectionStoreException` surfaces and the cohort object handed to
    /// `CommitCohortAsync` is completely unchanged by the failed attempt
    /// (A7/A9 in `LoreFetch.App` retain the pending cohort on this exact
    /// exception; this proves the store side of that contract holds), then
    /// releases the lock and commits the SAME cohort again, proving the
    /// retry actually lands.
    [Trait("Category", "WindowsOnly")]
    [Fact]
    public async Task RealCollectionStore_ThroughComposition_ThrowsWhileLocked_ThenCommitsOnceReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        var collectionDir = Path.Combine(Path.GetTempPath(), $"lorefetch-e2e-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(collectionDir);
        var collectionPath = Path.Combine(collectionDir, "collection.csv");

        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = new FolderFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            var store = new CsvCollectionStore(collectionPath, NullLogger<CsvCollectionStore>.Instance);

            // The exact real-classes composition path AppComposition.CreateRealAsync
            // wires — ComposeAsync is the testable core both Fakes and Real modes
            // share (AppComposition.cs), never a hand-wired shortcut.
            await using var session = await AppComposition.ComposeAsync(
                frameFactory,
                new StubCardDetector(cardCount: 1),
                new StubRectifier(),
                new StubCardIdentifier { NextDistances = [GoodDistance - 10] },
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance,
                ct,
                store: store);

            var cohort = await CaptureOnceFrameArrivesAsync(session, ct);
            Assert.Equal(TileState.Included, cohort.Tiles[0].State);

            // Baseline commit, so the file exists before we lock it, and so
            // "the retry lands" below is a meaningful quantity check (2), not
            // "the store is non-empty" trivially holding either way.
            var baselineCommitted = await session.Store!.CommitCohortAsync(cohort, ct);
            Assert.Equal(1, baselineCommitted);

            var lockedCohort = await CaptureOnceFrameArrivesAsync(session, ct);
            var stateBeforeLockedAttempt = lockedCohort.Tiles[0].State;
            var chosenBeforeLockedAttempt = lockedCohort.Tiles[0].Chosen;
            var distanceBeforeLockedAttempt = lockedCohort.Tiles[0].ChosenDistance;

            // FileShare.ReadWrite (no Delete): the read step and the .bak copy
            // both succeed, but the final File.Move — which must replace the
            // still-open target — fails on Windows because this locker was
            // never granted FileShare.Delete. Same reasoning as
            // CsvCollectionStoreWriteSequenceTests' WindowsOnly case.
            using (new FileStream(collectionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                await Assert.ThrowsAsync<CollectionStoreException>(
                    () => session.Store!.CommitCohortAsync(lockedCohort, ct));
            }

            // The cohort is retained, not cleared: nothing about the tile's
            // state, chosen candidate or distance was mutated by the failed
            // commit attempt. This is the store-side half of the A7/A9
            // "keep the pending cohort and let the user retry" contract.
            Assert.Equal(stateBeforeLockedAttempt, lockedCohort.Tiles[0].State);
            Assert.Equal(chosenBeforeLockedAttempt, lockedCohort.Tiles[0].Chosen);
            Assert.Equal(distanceBeforeLockedAttempt, lockedCohort.Tiles[0].ChosenDistance);

            // Lock released — the SAME cohort object commits successfully now.
            var retryCommitted = await session.Store!.CommitCohortAsync(lockedCohort, ct);
            Assert.Equal(1, retryCommitted);

            var rows = await session.Store!.ListAsync(ct);
            var row = Assert.Single(rows);
            Assert.Equal(2, row.Quantity);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
            try
            {
                Directory.Delete(collectionDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only — not the point of the test.
            }
        }
    }

    /// Waits for the pipeline (already running — `ComposeAsync` starts
    /// `RunAsync` exactly once) to process at least one fresh frame since
    /// the last capture, then captures. Mirrors `EndToEndTests`'
    /// `RunAndCaptureManyAsync`, minimized to "capture once" since this
    /// class never needs more than that per call.
    private static async Task<Cohort> CaptureOnceFrameArrivesAsync(AppSession session, CancellationToken ct)
    {
        var frameProcessedCount = 0;
        void OnFrameProcessed(CameraFrame _, LoreFetch.Core.Scanning.DetectionSnapshot __) =>
            Interlocked.Increment(ref frameProcessedCount);

        session.Pipeline.FrameProcessed += OnFrameProcessed;
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref frameProcessedCount) > 0, ct);
            var cohort = await session.Pipeline.CaptureAsync(ct);
            return cohort ?? throw new InvalidOperationException(
                "CaptureAsync returned null even though at least one frame with a detection was processed.");
        }
        finally
        {
            session.Pipeline.FrameProcessed -= OnFrameProcessed;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private static string NewCollectionStorePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-e2e-collection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "collection.csv");
    }

    /// A single included tile, built through `CohortTile`'s real public
    /// constructor (frozen `Core/Abstractions`) rather than a fake, so a
    /// store/exporter test exercises the exact same state machine the
    /// pipeline does. Mirrors `Tests/StreamD/CohortSupport.IncludedTile`,
    /// which this project cannot reference (a sibling test assembly).
    private static Cohort SingleTileCohort(string oracleId, string oracleName, int distance)
    {
        var tile = new CohortTile(
            DummyRectifiedCard(),
            [new CardCandidate(oracleId, oracleName, distance, ArtworkId: null)],
            GoodDistance,
            OkDistance);

        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, CaptureReason.Manual, [tile]);
    }

    private static RectifiedCard DummyRectifiedCard() => new(
        new byte[16],
        stride: 4,
        layout: PixelLayout.Bgr24,
        sourceQuad: new CardQuad(
            new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));
}
