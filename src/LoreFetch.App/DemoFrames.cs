using OpenCvSharp;

namespace LoreFetch.App;

/// Generates a small folder of synthetic, non-card placeholder images for
/// `FolderFrameSourceFactory` — the Fakes-mode frame source this app runs
/// against. CLAUDE.md bans committing card imagery (WotC IP either way,
/// Scryfall render or our own photo) and says even the fixture corpus must
/// stay local and gitignored. Rather than ship or require ANY image asset
/// at all, this draws a few solid-colour rectangles at runtime with
/// OpenCvSharp — already a `Core` dependency — into a fresh OS temp
/// directory. Nothing produced here resembles a card, and nothing is ever
/// written inside the repo.
internal static class DemoFrames
{
    private static readonly Scalar[] Palette =
    [
        new Scalar(60, 60, 200),   // BGR: a warm red-ish fill
        new Scalar(60, 160, 60),   // green
        new Scalar(180, 120, 40),  // blue-ish
    ];

    /// Creates a fresh temp folder with a handful of decodable PNGs and
    /// returns its path. Called once per app run; the OS temp directory is
    /// left for the OS to reclaim rather than adding teardown machinery —
    /// this is a demo path that is meant to outlive a crash for inspection,
    /// not a resource this process is obliged to clean up.
    public static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lorefetch-demo-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        const int width = 1280;
        const int height = 720;

        for (var i = 0; i < Palette.Length; i++)
        {
            using var mat = new Mat(height, width, MatType.CV_8UC3, Palette[i]);
            Cv2.Rectangle(
                mat,
                new Rect(width / 8, height / 8, (width * 3) / 4, (height * 3) / 4),
                new Scalar(255, 255, 255),
                thickness: 4);
            Cv2.PutText(
                mat,
                $"LoreFetch demo frame {i + 1}",
                new Point(width / 8 + 16, height / 8 + 56),
                HersheyFonts.HersheySimplex,
                1.2,
                new Scalar(255, 255, 255),
                thickness: 2);

            Cv2.ImWrite(Path.Combine(folder, $"frame-{i:D2}.png"), mat);
        }

        return folder;
    }
}
