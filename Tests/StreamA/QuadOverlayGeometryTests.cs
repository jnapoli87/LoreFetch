using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// A10 known bug 3: "the quad overlay is drawn about 35 px above the cards
/// when the preview is letterboxed top and bottom." <see cref="FrameToControlTransformTests"/>
/// already proves the pure <see cref="FrameToControlTransform"/> math is
/// correct, so the bug was in the wiring around it.
///
/// <para>
/// Root cause, found by driving the real <see cref="MainWindow"/> at known
/// window sizes and inspecting the actual <c>Bounds</c> Avalonia assigned:
/// with <c>Stretch="Uniform"</c>, <c>PreviewImage</c>'s OWN arrange rect is
/// already the tightly-fit, centred content rectangle — its
/// <c>Bounds.Size</c> is the post-letterbox size and <c>Bounds.Position</c>
/// is the centring offset within the cell, not the full cell itself. The old
/// <c>DrawQuadOverlay</c> fed that already-fitted size into
/// <c>FrameToControlTransform.Compute</c> as if it were the OUTER container
/// (re-applying the same uniform-fit math to an already-fitted rect, which
/// is why it silently computed ~1.0 scale and ~0 offset) and then discarded
/// <c>Bounds.Position</c> — the one number that actually mattered. The
/// quads were drawn relative to <c>QuadOverlayCanvas</c>'s own origin (the
/// FULL cell's top-left), landing at the top/left of the whole preview pane
/// instead of on the letterboxed image. Fixed by computing the transform
/// from <c>QuadOverlayCanvas.Bounds</c> instead — a plain <c>Canvas</c> has
/// no Stretch-mode arrange logic of its own, so it always reports the full
/// cell, which is the outer-container size <c>Compute</c> actually needs,
/// in the same coordinate origin the drawn <c>Polygon</c> points use.
/// </para>
///
/// <para>
/// These tests drive the REAL <see cref="MainWindow"/> — a real
/// <c>IScanPipeline.FrameProcessed</c> event, a real render pass, the real
/// <c>QuadOverlayCanvas</c> — and check the one thing that actually matters
/// for a user looking at the screen: does a drawn polygon corner land on the
/// exact WINDOW pixel the letterboxed image places that same frame corner
/// at? Comparing in each control's own LOCAL coordinates (as
/// <c>DrawQuadOverlay</c> itself does) cannot see an origin mismatch between
/// <c>PreviewImage</c> and <c>QuadOverlayCanvas</c> — translating both into
/// a shared coordinate space (the window) can, and is what caught this.
/// </para>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class QuadOverlayGeometryTests
{
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;

    /// A window shape TALL relative to its main-content box width (main
    /// content box aspect &lt; the 16:9 frame's aspect), so the frame's WIDTH
    /// binds and the content letterboxes top/bottom — the case the user
    /// actually reported ("drawn ~35 px too high").
    [AvaloniaFact]
    public async Task QuadOverlay_LetterboxedWindow_PolygonLandsExactlyOnFrameCorners()
    {
        await AssertOverlayMatchesTransform(windowWidth: 900, windowHeight: 1400, letterboxed: true);
    }

    /// The mirror case: a window shape WIDE relative to its main-content box
    /// height (box aspect &gt; the 16:9 frame's aspect), so the frame's
    /// HEIGHT binds and the content pillarboxes left/right instead — proves
    /// the fix isn't one-axis-only.
    [AvaloniaFact]
    public async Task QuadOverlay_PillarboxedWindow_PolygonLandsExactlyOnFrameCorners()
    {
        await AssertOverlayMatchesTransform(windowWidth: 1600, windowHeight: 900, letterboxed: false);
    }

    private static async Task AssertOverlayMatchesTransform(int windowWidth, int windowHeight, bool letterboxed)
    {
        var label = letterboxed ? "letterboxed" : "pillarboxed";

        var pipeline = new FirableScanPipeline();
        var settings = new ScanSettings();
        var source = new NullFrameSource();
        var session = new AppSession(pipeline, source, Task.CompletedTask, settings);
        var window = new MainWindow(session);
        window.Width = windowWidth;
        window.Height = windowHeight;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var previewImage = window.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(i => i.Name == "PreviewImage");
        var overlayCanvas = window.GetVisualDescendants().OfType<Avalonia.Controls.Canvas>()
            .FirstOrDefault(c => c.Name == "QuadOverlayCanvas");
        Assert.NotNull(previewImage);
        Assert.NotNull(overlayCanvas);

        // A quad with one corner away from the frame centre in both axes, so
        // a vertical-only or horizontal-only offset bug both show up.
        var quad = new CardQuad(
            new PointF2(100, 50),
            new PointF2(600, 80),
            new PointF2(620, 500),
            new PointF2(90, 470));

        // Warm-up frame: setting PreviewImage.Source for the FIRST time
        // invalidates its measure/arrange, but that new layout is not
        // committed until the NEXT layout pass — which happens after this
        // render callback returns, by which point OnFrameProcessed's single
        // frame has already been consumed. Firing (and fully pumping) one
        // frame first settles PreviewImage's real Stretch="Uniform" bounds
        // before the frame under test, matching the live app's steady state
        // (many frames in, not the very first one) rather than a
        // layout-timing race that would mask the actual bug under test.
        using var warmupFrame = MakeFrame(FrameWidth, FrameHeight);
        var warmupSnapshot = new DetectionSnapshot([], new FrameGeometry(FrameWidth, FrameHeight, 0), DateTimeOffset.UtcNow);
        pipeline.Fire(warmupFrame, warmupSnapshot);
        await PumpUntilAsync(() => previewImage!.Bounds.Width > 0 && previewImage.Bounds.Height > 0);

        using var frame = MakeFrame(FrameWidth, FrameHeight);
        var snapshot = new DetectionSnapshot([quad], new FrameGeometry(FrameWidth, FrameHeight, 0), DateTimeOffset.UtcNow);
        pipeline.Fire(frame, snapshot);

        // OnFrameProcessed posts the render to the UI thread via
        // Dispatcher.UIThread.Post + RequestAnimationFrame. A single
        // RunJobs() call does not pump an animation-frame callback headlessly
        // (A10CohortScreenshotTests hits the same thing driving the real
        // ScanPipeline) — force render-timer ticks and pump jobs, with real
        // delays between attempts, until the polygon actually lands or this
        // times out.
        Polygon? polygon = null;
        await PumpUntilAsync(() =>
        {
            polygon = overlayCanvas!.Children.OfType<Polygon>().FirstOrDefault();
            return polygon is not null;
        });

        var imageBounds = previewImage!.Bounds;
        var cellBounds = overlayCanvas!.Bounds;
        var debugInfo = $"window.ClientSize={window.ClientSize} PreviewImage.Bounds={imageBounds} OverlayCanvas.Bounds={cellBounds}";
        Assert.True(imageBounds.Width > 0 && imageBounds.Height > 0, $"[{label}] PreviewImage must have a non-zero size. {debugInfo}");

        // Sanity: confirm this window shape actually exercises the axis it
        // claims to. The FULL cell (QuadOverlayCanvas.Bounds) determines
        // which axis binds: a cell aspect below the 16:9 frame aspect means
        // the frame's width fills the cell and the height has slack
        // (letterbox, top/bottom bars); a cell aspect above it means the
        // reverse (pillarbox, left/right bars). If this fails, the window
        // size picked above no longer produces the case this test is named
        // for.
        var cellAspect = cellBounds.Width / cellBounds.Height;
        const double frameAspect = (double)FrameWidth / FrameHeight;
        if (letterboxed)
            Assert.True(cellAspect < frameAspect, $"[{label}] expected a cell narrower than 16:9 (letterbox), got {cellBounds}.");
        else
            Assert.True(cellAspect > frameAspect, $"[{label}] expected a cell wider than 16:9 (pillarbox), got {cellBounds}.");

        Assert.True(polygon is not null, $"[{label}] no polygon was drawn. {debugInfo} canvasChildren={overlayCanvas.Children.Count}");
        Assert.Equal(4, polygon!.Points.Count);

        // Ground truth, independent of whatever DrawQuadOverlay actually
        // does: fit the frame into PreviewImage's OWN rendered rect (which
        // Avalonia already tightly fits and centres for Stretch="Uniform")
        // and translate into WINDOW coordinates via PreviewImage's own
        // visual transform — this is exactly where the image's pixels are,
        // regardless of the overlay wiring.
        var expectedTransform = FrameToControlTransform.Compute(FrameWidth, FrameHeight, imageBounds.Width, imageBounds.Height);
        var frameCorners = new[] { quad.TL, quad.TR, quad.BR, quad.BL };

        for (var i = 0; i < 4; i++)
        {
            var expectedLocal = expectedTransform.Apply(frameCorners[i]);
            var expectedInWindow = previewImage.TranslatePoint(expectedLocal, window);
            Assert.NotNull(expectedInWindow);

            // Actual: the polygon point Avalonia is really drawing, in
            // QuadOverlayCanvas's own local space, translated into the SAME
            // window coordinate space via the Canvas's own visual transform.
            var actualLocal = polygon.Points[i];
            var actualInWindow = overlayCanvas.TranslatePoint(actualLocal, window);
            Assert.NotNull(actualInWindow);

            var dx = Math.Abs(expectedInWindow!.Value.X - actualInWindow!.Value.X);
            var dy = Math.Abs(expectedInWindow!.Value.Y - actualInWindow!.Value.Y);
            Assert.True(dx <= 1.0 && dy <= 1.0,
                $"[{label}] corner {i}: expected window point {expectedInWindow.Value}, actual {actualInWindow.Value} " +
                $"(dx={dx:F2}, dy={dy:F2}). {debugInfo}.");
        }

        window.Close();
    }

    /// Pumps the headless dispatcher and render timer until <paramref name="condition"/>
    /// is true or 5 seconds pass. Avalonia.Headless does not run a real
    /// composition/render loop on its own — <c>RunJobs()</c> drains posted
    /// Dispatcher work but not a `RequestAnimationFrame` callback, so
    /// `ForceRenderTimerTick()` is needed to actually fire one, and a real
    /// `Task.Delay` between attempts lets any background-thread posts land.
    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    private static CameraFrame MakeFrame(int width, int height)
    {
        const int bytesPerPixel = 3; // Bgr24
        var stride = width * bytesPerPixel;
        var buffer = new byte[stride * height];
        return new CameraFrame(buffer, width, height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, pool: null);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// Same shape as the other A-package NullScanPipeline fakes, except
    /// FrameProcessed can be raised on demand from the test.
    private sealed class FirableScanPipeline : IScanPipeline
    {
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
#pragma warning disable CS0067
        public event Action<Cohort>? AutoCaptured;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067
        public string SourceDescription => "headless-test";
        public Task<Cohort?> CaptureAsync(CancellationToken ct) => Task.FromResult<Cohort?>(null);
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Fire(CameraFrame frame, DetectionSnapshot snapshot) => FrameProcessed?.Invoke(frame, snapshot);
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
 * Chaos-test results (see CLAUDE.md "Chaos-test new regression tests"),
 * A10-bugs session, 2026-09-22: re-applied the original bug by reverting
 * DrawQuadOverlay's `var bounds = QuadOverlayCanvas.Bounds;` back to
 * `var bounds = PreviewImage.Bounds;`, ran ONLY these two tests, and both
 * failed for the right reason — a coordinate mismatch on the exact axis
 * each window shape exercises:
 *   [letterboxed]  corner 0: expected (32.29, 442.15), actual (32.29, 56.15)
 *                  (dx=0.00, dy=386.00)
 *   [pillarboxed]  corner 0: expected (165.5, 68.75), actual (57.5, 68.75)
 *                  (dx=108.00, dy=0.00)
 * — i.e. the drawn polygon sat at the top/left of the whole preview pane
 * instead of on the letterboxed/pillarboxed image content, which is exactly
 * "the overlay is drawn ~35 px too high" scaled up by this test's more
 * extreme window proportions. The fix was then reverted back to
 * `QuadOverlayCanvas.Bounds` and both tests pass again.
 *
 * An earlier version of this chaos test (before the two-frame warm-up was
 * added) failed on the reverted code for the WRONG reason — no polygon was
 * drawn at all, canvasChildren=0 — because PreviewImage.Bounds only settles
 * to its real Stretch="Uniform" size on the layout pass AFTER Source is
 * first assigned, and OnFrameProcessed's single fired frame had already been
 * consumed by then (FrameHandoff is single-take). Firing a warm-up frame
 * first and waiting for PreviewImage.Bounds to become non-zero before firing
 * the frame under test reproduces the live app's steady state (many frames
 * in, not the very first one) and gets the coordinate-mismatch failure that
 * actually matches the reported bug.
 */
