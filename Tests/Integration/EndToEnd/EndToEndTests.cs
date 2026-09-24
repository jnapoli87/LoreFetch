using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// The end-to-end suite (package S0.6a) — docs/TESTING.md §"Integration
/// (fakes, then real)": frame source → detector → rectifier → identifier →
/// `Cohort` → `CommitCohortAsync` → assert the store, all wired through the
/// REAL composition path (`ScanPipelineFactory.Create` and
/// `IFrameSourceFactory.CreateAsync`), never a hand-wired shortcut — the
/// whole point of this suite is to catch a broken contract, and a shortcut
/// could paper over exactly the wiring bug it exists to catch.
///
/// Every case is a `[Theory]` over `ImplementationSetKind` (finding V8):
/// `Fakes` runs today; `Real` skips — or fails, under
/// `LOREFETCH_REQUIRE_REAL` (finding V7) — with a stated reason until
/// integration wires it in.
public class EndToEndTests
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task ExcludedTile_IsAbsentFromTheCommittedStore(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }), // confident match
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 1, ct);
            var cohort = cohorts[0];
            Assert.NotNull(cohort);
            Assert.Equal(TileState.Included, cohort!.Tiles[0].State); // sanity: there IS something to exclude

            cohort.Tiles[0].ToggleExcluded();
            Assert.Equal(TileState.Excluded, cohort.Tiles[0].State);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            var cardsCommitted = await store.CommitCohortAsync(cohort, ct);
            var rows = await store.ListAsync(ct);

            Assert.Equal(0, cardsCommitted);
            Assert.Empty(rows);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task Discard_NeverCommittingACohort_LeavesTheStoreUnchanged(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }),
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var store = set.CreateCollectionStore(NewCollectionStorePath());

            // Baseline: commit a real cohort first, so "unchanged" below is
            // a meaningful assertion rather than "the store is still empty"
            // trivially holding regardless of whether discard does
            // anything at all.
            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 2, ct);
            await store.CommitCohortAsync(cohorts[0]!, ct);
            var baselineRows = await store.ListAsync(ct);
            Assert.NotEmpty(baselineRows);

            // Escape: the second cohort is simply never committed. There is
            // no "discard" API call — the omission IS the discard.
            Assert.NotNull(cohorts[1]);

            var rowsAfterDiscard = await store.ListAsync(ct);
            Assert.Equal(baselineRows, rowsAfterDiscard);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task ManuallySetTile_CommitsAsSourceManual_WithNullBestMatchDistance(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            // Beyond OkDistance -> Unresolved: nothing is proposed, so the
            // manual pick below is unambiguously the user's correction, not
            // a coincidental echo of the machine's own guess.
            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { OkDistance + 50 }),
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 1, ct);
            var cohort = cohorts[0];
            Assert.NotNull(cohort);
            Assert.Equal(TileState.Unresolved, cohort!.Tiles[0].State);

            var catalog = set.CreateOracleCatalog();
            var manualEntry = catalog.All[0];
            cohort.Tiles[0].SetManually(manualEntry);
            Assert.Equal(TileState.ManuallySet, cohort.Tiles[0].State);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            await store.CommitCohortAsync(cohort, ct);
            var rows = await store.ListAsync(ct);

            var row = Assert.Single(rows);
            Assert.Equal(manualEntry.OracleId, row.OracleId);
            Assert.Equal(RowSource.Manual, row.Source);
            Assert.Null(row.BestMatchDistance);

            // DECISIONS.md "Contract change — ArtworkId": a manual pick names a
            // card, not an art, so ArtworkId must be null here regardless of
            // implementation set — never a fabricated printing id.
            Assert.Null(row.ArtworkId);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task ClearedTile_CommitsTheMachinesProposalAgain(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }), // confident machine proposal
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 1, ct);
            var cohort = cohorts[0];
            Assert.NotNull(cohort);
            var tile = cohort!.Tiles[0];
            Assert.Equal(TileState.Included, tile.State);

            var originalChosen = tile.Chosen!.Value;
            var originalDistance = tile.ChosenDistance;
            var originalArtworkId = tile.ChosenArtworkId;

            var catalog = set.CreateOracleCatalog();
            var manualEntry = catalog.All[0];
            Assert.NotEqual(originalChosen.OracleId, manualEntry.OracleId); // must be a genuinely different card

            tile.SetManually(manualEntry);
            Assert.Equal(TileState.ManuallySet, tile.State);

            tile.Clear();
            Assert.Equal(TileState.Included, tile.State);
            Assert.Equal(originalChosen, tile.Chosen);
            Assert.Equal(originalDistance, tile.ChosenDistance);

            // DECISIONS.md "Artwork granularity": identity must be checked at
            // the artwork level, not just OracleId — a wrong sibling art of
            // the same oracle card would still pass the OracleId checks below.
            Assert.Equal(originalArtworkId, tile.ChosenArtworkId);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            await store.CommitCohortAsync(cohort, ct);
            var rows = await store.ListAsync(ct);

            var row = Assert.Single(rows);
            Assert.Equal(originalChosen.OracleId, row.OracleId);
            Assert.Equal(RowSource.Hash, row.Source);
            Assert.Equal(originalDistance, row.BestMatchDistance);
            Assert.Equal(originalArtworkId, row.ArtworkId);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task RecommittingTheSameCard_IncrementsQuantity_RatherThanAddingARow(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }), // same proposal both times
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 2, ct);
            var store = set.CreateCollectionStore(NewCollectionStorePath());

            var firstCommitted = await store.CommitCohortAsync(cohorts[0]!, ct);
            var secondCommitted = await store.CommitCohortAsync(cohorts[1]!, ct);

            Assert.Equal(1, firstCommitted);
            Assert.Equal(1, secondCommitted);

            var rows = await store.ListAsync(ct);
            var row = Assert.Single(rows);
            Assert.Equal(2, row.Quantity);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task PartialCohort_SevenOfNineExpected_Commits7(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 9, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 7), // only 7 detected, 9 expected
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }),
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 1, ct);
            var cohort = cohorts[0];
            Assert.NotNull(cohort);

            // Space captures whatever is detected, ignoring ExpectedCount —
            // CONTRACTS.md's CaptureAsync doc: "Ignores ExpectedCount".
            Assert.Equal(9, cohort!.ExpectedCount);
            Assert.Equal(7, cohort.Tiles.Count);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            var cardsCommitted = await store.CommitCohortAsync(cohort, ct);

            Assert.Equal(7, cardsCommitted);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    /// Not one of S0.6a's six required cases, but exercises the fourth
    /// deliverable the package spec calls for (`ScriptedTrigger`, finding
    /// V9) through the same real composition path: without this, the class
    /// would be built and never actually driven by any test in this
    /// package.
    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task ScriptedTrigger_FiringOnTheConfiguredCall_RaisesAutoCaptured_AndCommits(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(10));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            var trigger = new ScriptedTrigger(fireOnCall: 3);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }),
                trigger,
                settings,
                NullLoggerFactory.Instance);

            Cohort? autoCohort = null;
            pipeline.AutoCaptured += cohort => autoCohort = cohort;

            using var loopCts = new CancellationTokenSource();
            var runTask = pipeline.RunAsync(loopCts.Token);
            try
            {
                await WaitUntilAsync(() => Volatile.Read(ref autoCohort) is not null, ct);
            }
            finally
            {
                await loopCts.CancelAsync();
                await AwaitLoopShutdown(runTask);
            }

            Assert.NotNull(autoCohort);
            Assert.Equal(CaptureReason.AutoSettle, autoCohort!.Reason);
            Assert.Equal(1, trigger.NotifyCapturedCount);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            var cardsCommitted = await store.CommitCohortAsync(autoCohort, ct);
            Assert.Equal(1, cardsCommitted);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    /// Package S0.6b's regression guard for the old `RectifiedCard`
    /// disposal footgun (docs/CONTRACTS.md §"Why `RectifiedCard` is not
    /// pooled" — "removed by construction"): a tile's `Image` is plain
    /// managed memory, never pooled, never `IDisposable`, so nothing in the
    /// commit path has any business touching it. Assert on the actual pixel
    /// bytes, not merely that the reference is still non-null — a null check
    /// would pass even if something cleared the buffer in place.
    [Theory]
    [MemberData(nameof(ImplementationSets.All), MemberType = typeof(ImplementationSets))]
    public async Task Tile_ThumbnailImage_IsStillReadable_AfterCohortCommits(ImplementationSetKind kind)
    {
        var set = ImplementationSets.Create(kind);
        set.EnsureAvailable();

        var ct = TestContext.Current.CancellationToken;
        var frameDir = SyntheticFrames.CreateTempDirectory();
        try
        {
            var settings = new ScanSettings { ExpectedCount = 1, GoodDistance = GoodDistance, OkDistance = OkDistance };
            var frameFactory = set.CreateFrameSourceFactory(frameDir, TimeSpan.FromMilliseconds(15));
            await using var source = await frameFactory.CreateAsync(settings, ct);

            await using var pipeline = ScanPipelineFactory.Create(
                source,
                set.CreateDetector(cardCount: 1),
                set.CreateRectifier(),
                set.CreateIdentifier(new[] { GoodDistance - 10 }),
                new ScriptedTrigger(fireOnCall: int.MaxValue),
                settings,
                NullLoggerFactory.Instance);

            var cohorts = await RunAndCaptureManyAsync(pipeline, times: 1, ct);
            var cohort = cohorts[0];
            Assert.NotNull(cohort);
            var tile = cohort!.Tiles[0];

            // A deep copy taken BEFORE commit, not a reference into the same
            // backing array — otherwise an in-place corruption during commit
            // would corrupt this "before" snapshot too, and the comparison
            // below would trivially agree with itself no matter what
            // happened.
            var beforeCommit = tile.Image.Pixels.ToArray();

            // Guard against a vacuous pass on an all-one-value buffer: the
            // synthetic frame is a flat fill with a drawn rectangle, so a
            // real crop of it must contain more than one distinct byte.
            var firstByte = beforeCommit[0];
            Assert.Contains(beforeCommit, b => b != firstByte);

            var store = set.CreateCollectionStore(NewCollectionStorePath());
            await store.CommitCohortAsync(cohort, ct);

            var afterCommit = tile.Image.Pixels.ToArray();
            Assert.Equal(beforeCommit, afterCommit);
        }
        finally
        {
            SyntheticFrames.DeleteDirectory(frameDir);
        }
    }

    /// Starts `pipeline.RunAsync` in the background, then — `times` times —
    /// waits for a FRESH frame since the last capture (not merely "at least
    /// one frame ever") and calls `CaptureAsync` once. `FolderFrameSource`
    /// cycles forever on its own timer, so unlike `ScanPipelineFactoryTests`'
    /// finite `OneShotFrameSource`, `RunAsync` never completes on its own
    /// here — the loop is always stopped by cancelling `loopCts` in the
    /// `finally`, mirroring `ScanPipelineTests`' same concurrency dance.
    private static async Task<IReadOnlyList<Cohort?>> RunAndCaptureManyAsync(
        IScanPipeline pipeline, int times, CancellationToken ct)
    {
        var frameProcessedCount = 0;
        pipeline.FrameProcessed += (_, _) => Interlocked.Increment(ref frameProcessedCount);

        using var loopCts = new CancellationTokenSource();
        var runTask = pipeline.RunAsync(loopCts.Token);

        var results = new List<Cohort?>(times);
        try
        {
            var lastObserved = 0;
            for (var i = 0; i < times; i++)
            {
                await WaitUntilAsync(() => Volatile.Read(ref frameProcessedCount) > lastObserved, ct);
                results.Add(await pipeline.CaptureAsync(ct));
                lastObserved = Volatile.Read(ref frameProcessedCount);
            }
        }
        finally
        {
            await loopCts.CancelAsync();
            await AwaitLoopShutdown(runTask);
        }

        return results;
    }

    /// A fresh `collection.csv` path under a fresh temp directory, for
    /// `IImplementationSet.CreateCollectionStore`. `Fakes` ignores the path
    /// entirely (`StubCollectionStore` is in-memory), so this only matters
    /// once a case actually reaches `Real`'s `CsvCollectionStore` — giving
    /// every call its own directory means these cases never collide with
    /// each other, or with a run in progress elsewhere, once that happens.
    private static string NewCollectionStorePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-e2e-collection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "collection.csv");
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

    /// Awaits a loop task that was stopped via cancellation, treating
    /// OperationCanceledException as the expected shutdown outcome rather
    /// than a test failure (same helper shape as ScanPipelineTests').
    private static async Task AwaitLoopShutdown(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop observed the cancellation it was asked for.
        }
    }
}
