using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// `CountingArrayPool` lives in CameraFrameTests.cs (same namespace) and is
/// reused here rather than duplicated.
public class FolderFrameSourceTests
{
    // Distinct BGR fills so a decoded frame's own top-left pixel identifies
    // which source image it came from.
    private static readonly Scalar[] Palette =
    [
        new Scalar(0, 0, 255), // red
        new Scalar(0, 255, 0), // green
        new Scalar(255, 0, 0), // blue
    ];

    [Fact]
    public async Task ReadAsync_YieldsFrames_WithAdvertisedLayoutAndGeometry()
    {
        var dir = CreateTempImageDirectory(imageCount: 1, width: 64, height: 96);
        try
        {
            await using var source = FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(10));

            Assert.Equal(64, source.Geometry.Width);
            Assert.Equal(96, source.Geometry.Height);
            Assert.Equal(0, source.Geometry.RotationDegrees);
            Assert.Contains(new DirectoryInfo(dir).Name, source.Description);
            Assert.Contains("1 images", source.Description);

            var enumerator = source.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                var frame = enumerator.Current;

                Assert.Equal(PixelLayout.Bgr24, frame.Layout);
                Assert.Equal(64, frame.Width);
                Assert.Equal(96, frame.Height);
                Assert.Equal(64 * 3, frame.Stride);
                Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);

                frame.Dispose();
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    /// The source advances on its own timer and drops frames into a
    /// capacity-1 `DropOldest` channel, so read k is NOT image k mod N: a
    /// reader that falls one interval behind (a GC pause, a loaded CI runner)
    /// skips an image. Asserting on exact positions made this test flaky —
    /// red on macOS CI roughly one run in three, and 1 in 8 locally.
    ///
    /// What IS guaranteed under drops: within one pass the image index only
    /// rises, so an index that goes DOWN between two reads can only mean the
    /// source wrapped. A source that stops at the last image never produces
    /// that decrease, whatever gets dropped.
    [Fact]
    public async Task ReadAsync_CyclesBackToTheFirstImage_AfterMoreThanNReads()
    {
        const int imageCount = 3;
        const int maxReads = 100; // ~1.5 s at 15 ms; a healthy run needs about 4
        var dir = CreateTempImageDirectory(imageCount);
        try
        {
            await using var source = FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(15));

            var seen = new List<int>();
            var wrapped = false;
            var enumerator = source.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                while (seen.Count < maxReads && !(wrapped && seen.Distinct().Count() == imageCount))
                {
                    Assert.True(await enumerator.MoveNextAsync(), "the source stopped yielding instead of cycling");
                    var frame = enumerator.Current;
                    var index = ImageIndexOf(frame);
                    frame.Dispose();

                    if (seen.Count > 0 && index < seen[^1])
                        wrapped = true;
                    seen.Add(index);
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            var trace = string.Join(",", seen);
            Assert.True(wrapped, $"no wrap observed in {seen.Count} reads; image indices seen: {trace}");
            Assert.True(seen.Distinct().Count() == imageCount, $"not every image was served; image indices seen: {trace}");
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task ReadAsync_DisposesEveryDroppedFrame_RentsEqualReturnsAfterDispose()
    {
        var pool = new CountingArrayPool();
        var dir = CreateTempImageDirectory(imageCount: 1);
        try
        {
            var source = FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(5), pool);

            var enumerator = source.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                // A deliberately slow consumer: sleeping between reads lets the
                // fast (5ms) producer tick — and drop — several times per read.
                for (var i = 0; i < 5; i++)
                {
                    Assert.True(await enumerator.MoveNextAsync());
                    var frame = enumerator.Current;
                    await Task.Delay(40, TestContext.Current.CancellationToken);
                    frame.Dispose();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            await source.DisposeAsync();

            // More rents than the 5 frames this test explicitly disposed
            // proves drops actually happened — otherwise this test would
            // pass trivially with nothing dropped at all.
            Assert.True(pool.RentCount > 5, $"expected drops to have occurred; RentCount was {pool.RentCount}");
            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task DisposeAsync_ReleasesEverything_RentsEqualReturns()
    {
        var pool = new CountingArrayPool();
        var dir = CreateTempImageDirectory(imageCount: 1);
        try
        {
            var source = FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(5), pool);

            // Let the background loop produce (and self-drop) a handful of
            // frames without anyone ever calling ReadAsync. Wait for the
            // rents themselves rather than a fixed delay: the first frame's
            // decode can take longer than any fixed guess on a cold CI
            // runner, which made a 60 ms wait flaky on macos-latest. Three
            // rents with no reader means the capacity-1 channel has already
            // dropped at least two frames.
            await WaitUntilAsync(() => pool.RentCount >= 3, TimeSpan.FromSeconds(10),
                () => $"the producer loop rented only {pool.RentCount} buffers in 10 s");

            await source.DisposeAsync();

            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public void Open_MissingDirectory_ThrowsFrameSourceException()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"lorefetch-missing-{Guid.NewGuid():N}");

        Assert.Throws<FrameSourceException>(() => FolderFrameSource.Open(missingDir, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Open_DirectoryWithNoImageFiles_ThrowsFrameSourceException()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-noimages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "not-an-image.txt"), "hello");

            Assert.Throws<FrameSourceException>(() => FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public void Open_DirectoryWithOnlyCorruptImage_ThrowsFrameSourceException()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Right extension, undecodable content.
            File.WriteAllBytes(Path.Combine(dir, "bad.png"), [0x00, 0x01, 0x02, 0x03]);

            Assert.Throws<FrameSourceException>(() => FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task Factory_CreateAsync_OpensAFolderSource()
    {
        var dir = CreateTempImageDirectory(imageCount: 2);
        try
        {
            var factory = new FolderFrameSourceFactory(dir, TimeSpan.FromMilliseconds(50));

            await using var source = await factory.CreateAsync(new ScanSettings(), TestContext.Current.CancellationToken);

            Assert.Contains("2 images", source.Description);
        }
        finally
        {
            DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task Factory_CreateAsync_MissingFolder_ThrowsFrameSourceException()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"lorefetch-missing-{Guid.NewGuid():N}");
        var factory = new FolderFrameSourceFactory(missingDir, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<FrameSourceException>(
            () => factory.CreateAsync(new ScanSettings(), TestContext.Current.CancellationToken));
    }

    private static string CreateTempImageDirectory(int imageCount, int width = 64, int height = 96)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lorefetch-folderframesource-{Guid.NewGuid():N}");
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

    /// Which source image a frame came from, by its top-left pixel's full BGR
    /// value — no single channel is enough, since red and green both have B=0.
    private static int ImageIndexOf(CameraFrame frame)
    {
        var (b, g, r) = (frame.Pixels.Span[0], frame.Pixels.Span[1], frame.Pixels.Span[2]);
        var index = Array.FindIndex(Palette, c => c.Val0 == b && c.Val1 == g && c.Val2 == r);
        Assert.True(index >= 0, $"top-left pixel ({b},{g},{r}) matches no palette colour");
        return index;
    }

    /// Polls `condition` until it holds, failing with `describe()` if it has
    /// not within `timeout`. For waiting on the source's background loop
    /// without guessing how long a cold machine takes to get going.
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, describe());
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static void DeleteTempDirectory(string dir)
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
