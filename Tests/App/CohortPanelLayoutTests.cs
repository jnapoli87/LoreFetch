using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// #17: the cohort panel was a fixed 280 px column of 80 px tiles with a
/// 9 pt trimmed name, so a proposed match couldn't be read, let alone checked,
/// before Enter. These tests pin what validation needs, at the default
/// 1024 × 768 window: a 3 × 3 cohort shows as 3 × 3 with every tile visible,
/// each name sits above its thumbnail (next to the card's printed title) and
/// reads in full, and the divider can be dragged.
/// </summary>
public class CohortPanelLayoutTests
{
    private const int Good = 100;
    private const int Ok = 200;

    /// A long name and a split card's name: the two shapes that were cut
    /// off to "…" before.
    private static readonly string[] LongNames =
    [
        "Kozilek, the Great Distortion",
        "Sagu Wildling // Roost Seek",
        "Asmoranomardicadaistinaculdacar",
    ];

    [AvaloniaFact]
    public void NineTiles_FormThreeByThree_AllInsideTheVisiblePanel()
    {
        var window = ShowWithCohort(MakeCohort(9, i => $"Card {i}"));

        var panel = FindNamed<Border>(window, "CohortPanel");
        var panelRect = WindowRect(panel, window);
        var tiles = FindTiles(window);
        Assert.Equal(9, tiles.Count);

        var rects = tiles.Select(t => WindowRect(t, window)).ToList();
        Assert.Equal(3, rects.Select(r => Math.Round(r.X)).Distinct().Count());
        Assert.Equal(3, rects.Select(r => Math.Round(r.Y)).Distinct().Count());

        foreach (var r in rects)
        {
            Assert.True(
                r.X >= panelRect.X - 0.5 && r.Right <= panelRect.Right + 0.5 &&
                r.Y >= panelRect.Y - 0.5 && r.Bottom <= panelRect.Bottom + 0.5,
                $"Tile {r} must sit inside the visible cohort panel {panelRect}, with no scrolling.");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void TileName_SitsAboveTheThumbnail()
    {
        var window = ShowWithCohort(MakeCohort(9, i => $"Card {i}"));

        foreach (var tile in FindTiles(window))
        {
            var name = FindNamed<TextBlock>(tile, "TileName");
            var thumbnail = FindNamed<Image>(tile, "TileThumbnail");
            var nameRect = WindowRect(name, window);
            var thumbRect = WindowRect(thumbnail, window);

            Assert.True(thumbRect.Height > 0, "Precondition: the thumbnail must render (a full-size card).");
            Assert.True(
                nameRect.Bottom <= thumbRect.Top + 0.5,
                $"The name ({nameRect}) must sit above the thumbnail ({thumbRect}), next to the printed title.");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void LongNames_RenderInFull_AtAReadableSize()
    {
        var window = ShowWithCohort(MakeCohort(9, i => LongNames[i % LongNames.Length]));

        foreach (var tile in FindTiles(window))
        {
            var name = FindNamed<TextBlock>(tile, "TileName");
            Assert.True(name.FontSize >= 14, $"Names must be at least 14 px to read; got {name.FontSize}.");

            // Lay the text out wrapped at the width the block was actually
            // given; it must fit in the height the block was actually given.
            // A trimmed or single-line block is one line tall and fails this.
            var typeface = new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch);
            var wrapped = new TextLayout(
                name.Text ?? string.Empty, typeface, name.FontSize, name.Foreground,
                textWrapping: TextWrapping.Wrap, maxWidth: name.Bounds.Width);

            Assert.True(
                wrapped.Height <= name.Bounds.Height + 0.5,
                $"'{name.Text}' needs {wrapped.Height:F1}px at {name.Bounds.Width:F1}px wide " +
                $"but was only given {name.Bounds.Height:F1}px: it is cut off.");
            Assert.True(
                wrapped.TextLines.All(l => l.WidthIncludingTrailingWhitespace <= name.Bounds.Width + 0.5),
                $"'{name.Text}' has a line wider than its block ({name.Bounds.Width:F1}px): it is cut off.");

            var nameRect = WindowRect(name, window);
            var tileRect = WindowRect(tile, window);
            Assert.True(
                nameRect.Bottom <= tileRect.Bottom + 0.5 && nameRect.Right <= tileRect.Right + 0.5,
                $"'{name.Text}' ({nameRect}) spills outside its tile ({tileRect}).");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void DraggingTheDivider_WidensTheCohortPanel()
    {
        var window = ShowWithCohort(MakeCohort(9, i => $"Card {i}"));

        var splitter = FindNamed<GridSplitter>(window, "CohortSplitter");
        var panel = FindNamed<Border>(window, "CohortPanel");
        var before = panel.Bounds.Width;

        var grip = splitter.TranslatePoint(
            new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, MouseButton.Left);
        window.MouseMove(grip - new Point(150, 0));
        window.MouseUp(grip - new Point(150, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            panel.Bounds.Width > before + 100,
            $"Dragging the divider 150 px left must widen the cohort panel; was {before:F0}, now {panel.Bounds.Width:F0}.");

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MainWindow ShowWithCohort(Cohort cohort)
    {
        var session = new AppSession(new NullScanPipeline(), new NullFrameSource(), Task.CompletedTask, new ScanSettings());
        var window = new MainWindow(session) { Width = 1024, Height = 768 };
        ((LoreFetch.App.ViewModels.MainViewModel)window.DataContext!).LoadCohort(cohort);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// A full-size card, so the thumbnail renders at its real aspect ratio
    /// and takes the room it would in the live app.
    private static RectifiedCard MakeFullSizeCard()
    {
        const int stride = RectifiedCard.CanonicalWidth * 4;
        var pixels = new byte[stride * RectifiedCard.CanonicalHeight];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new RectifiedCard(pixels, stride, PixelLayout.Bgra32, default);
    }

    private static Cohort MakeCohort(int count, Func<int, string> name)
    {
        var card = MakeFullSizeCard();
        var tiles = Enumerable.Range(0, count)
            .Select(i => new CohortTile(card, [new CardCandidate($"oracle-{i}", name(i), 50, ArtworkId: null)], Good, Ok))
            .ToList();
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, count, CaptureReason.Manual, tiles);
    }

    private static List<Grid> FindTiles(Visual root) =>
        root.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "CohortTile").ToList();

    private static T FindNamed<T>(Visual root, string name) where T : Control
    {
        var found = root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.NotNull(found);
        return found!;
    }

    private static Rect WindowRect(Visual v, Visual window)
    {
        var topLeft = v.TranslatePoint(default, window)!.Value;
        return new Rect(topLeft, v.Bounds.Size);
    }

    private sealed class NullScanPipeline : IScanPipeline
    {
#pragma warning disable CS0067 // Events required by the interface but never raised by this stub
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
