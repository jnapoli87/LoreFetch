using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.Fakes;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using LoreFetch.Core.Scanning;
using LoreFetch.Core.Trigger;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// A10-prep item 5's one new screenshot test: a 9-card cohort, captured with
/// Space through the SAME window-level keyboard path A7's
/// <see cref="KeyboardCaptureTests"/> drive, running the REAL
/// <see cref="ScanPipeline"/> with the demo wiring (item 4a's
/// <see cref="LayoutFollowingCardDetector"/>, item 4b's
/// <see cref="DemoCardIdentifier"/>) — not a spy. Saves
/// <c>A10-cohort-9.png</c> to the test output directory and asserts, via the
/// visual tree, that the grid holds 9 tiles across at least 2 distinct
/// states.
/// </summary>
public class A10CohortScreenshotTests
{
    [AvaloniaFact]
    public async Task NineCardCohort_CapturedViaSpace_ShowsNineTilesInAtLeastTwoStates_AndSavesScreenshot()
    {
        var ct = TestContext.Current.CancellationToken;

        var settings = new ScanSettings
        {
            ExpectedCount = 9,
            GoodDistance = DemoThresholds.GoodDistance,
            OkDistance = DemoThresholds.OkDistance,
        };

        var detector = new LayoutFollowingCardDetector(new StubCardDetector(9), settings);
        var rectifier = new StubRectifier();
        var identifier = new DemoCardIdentifier(new StubCardIdentifier(), settings);
        var trigger = new AutoCaptureTrigger(settings);

        using var frame = MakeFrame();
        var source = new OneFrameThenWaitSource(frame);

        var pipeline = new ScanPipeline(source, detector, rectifier, identifier, trigger, settings);

        // Subscribe BEFORE starting the loop so a fast first frame can't be
        // missed — CaptureAsync (Space) needs a retained frame to work with.
        var frameProcessedTcs = new TaskCompletionSource();
        pipeline.FrameProcessed += (_, _) => frameProcessedTcs.TrySetResult();

        var runTask = pipeline.RunAsync(ct);
        var session = new AppSession(pipeline, source, runTask, settings);

        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            await frameProcessedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            var vm = (MainViewModel)window.DataContext!;
            Assert.Empty(vm.Tiles); // nothing before Space

            // Drive Space through the real window-level tunnel handler — the
            // same path A7's KeyboardCaptureTests use — rather than calling
            // MainViewModel.CaptureFromPipelineAsync directly.
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);

            // The real ScanPipeline.CaptureAsync runs its rectify+identify
            // loop on a background Task.Run, so — unlike the SpyPipeline the
            // A7 tests use, which completes synchronously — a single
            // Dispatcher.UIThread.RunJobs() right after KeyPressQwerty is not
            // enough. Pump the dispatcher until the tiles actually land.
            await WaitUntilAsync(() => vm.Tiles.Count == 9, TimeSpan.FromSeconds(10));

            Assert.Equal(9, vm.Tiles.Count);

            var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);

            var outDir = AppContext.BaseDirectory;
            var pngPath = Path.Combine(outDir, "A10-cohort-9.png");
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

            // Assert via the visual tree: find the cohort grid, and confirm
            // it holds 9 tiles spanning at least 2 distinct TileStates. The
            // demo identifier cycles confident / low-confidence / unresolved
            // every 3 calls, which for 9 tiles yields 3 of each — Included
            // (confident and low-confidence both report Included) and
            // Unresolved, so this is exactly 2 distinct TileState values.
            var cohortGrid = window.GetVisualDescendants()
                .OfType<ItemsControl>()
                .FirstOrDefault(ic => ic.Name == "CohortGrid");
            Assert.NotNull(cohortGrid);

            var tiles = cohortGrid.ItemsSource as ObservableCollection<TileViewModel>;
            Assert.NotNull(tiles);
            Assert.Equal(9, tiles.Count);

            var distinctStates = tiles.Select(t => t.State).Distinct().Count();
            Assert.True(
                distinctStates >= 2,
                $"Expected the 9-tile cohort to span at least 2 distinct states, got {distinctStates} " +
                $"({string.Join(", ", tiles.Select(t => t.State).Distinct())}).");

            // Also confirm at least one low-confidence tile is present —
            // it's a real reachable state that "2 distinct TileStates" alone
            // wouldn't prove (Included covers both confident and
            // low-confidence).
            Assert.Contains(tiles, t => t.IsLowConfidence);
        }
        finally
        {
            window.Close();
            await pipeline.DisposeAsync();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (FrameSourceException)
            {
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static CameraFrame MakeFrame() =>
        new(
            buffer: new byte[640 * 480 * 3],
            width: 640,
            height: 480,
            stride: 640 * 3,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: null);

    /// Yields exactly one frame, then blocks (until cancelled) rather than
    /// completing the enumeration — RunAsync's loop must stay alive so the
    /// pipeline keeps holding the retained frame for Space's CaptureAsync.
    private sealed class OneFrameThenWaitSource : IFrameSource
    {
        private readonly CameraFrame _frame;

        public OneFrameThenWaitSource(CameraFrame frame) => _frame = frame;

        public string Description => "one-frame-test";

        public FrameGeometry Geometry => new(_frame.Width, _frame.Height, 0);

        public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return _frame;

            var tcs = new TaskCompletionSource();
            await using var registration = ct.Register(() => tcs.TrySetResult());
            await tcs.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
