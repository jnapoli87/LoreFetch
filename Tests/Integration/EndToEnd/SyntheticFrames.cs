using OpenCvSharp;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Writes plain colour-fill images with a drawn rectangle to a fresh temp
/// folder, for `FolderFrameSource` to decode. `StubCardDetector` ignores
/// pixel content entirely, so a flat fill is sufficient to exercise the real
/// demo path with zero card imagery ever touching git (docs/TESTING.md
/// §"1. The fakes make an end-to-end test possible on day zero" — "plain
/// fills with a drawn rectangle, written to a temp folder"). Never write
/// under the repo; always under `Path.GetTempPath()`, and always clean up.
public static class SyntheticFrames
{
    private static readonly Scalar[] Palette =
    [
        new Scalar(40, 40, 200), // BGR, not a real card — content is irrelevant to every stub
        new Scalar(40, 200, 40),
        new Scalar(200, 40, 40),
    ];

    public static string CreateTempDirectory(int imageCount = 1, int width = 480, int height = 360)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        for (var i = 0; i < imageCount; i++)
        {
            using var mat = new Mat(height, width, MatType.CV_8UC3, Palette[i % Palette.Length]);
            Cv2.Rectangle(
                mat,
                new Rect(width / 4, height / 4, width / 2, height / 2),
                new Scalar(255, 255, 255),
                thickness: 2);
            Cv2.ImWrite(Path.Combine(dir, $"{i:D2}.png"), mat);
        }

        return dir;
    }

    public static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; not the point of the test.
        }
    }
}
