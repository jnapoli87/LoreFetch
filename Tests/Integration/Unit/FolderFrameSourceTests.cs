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

    [Fact]
    public async Task ReadAsync_CyclesBackToTheFirstImage_AfterMoreThanNReads()
    {
        const int imageCount = 3;
        var dir = CreateTempImageDirectory(imageCount);
        try
        {
            await using var source = FolderFrameSource.Open(dir, TimeSpan.FromMilliseconds(15));

            var seenTopLeftBlue = new List<byte>();
            var enumerator = source.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                for (var i = 0; i < imageCount + 4; i++)
                {
                    Assert.True(await enumerator.MoveNextAsync());
                    var frame = enumerator.Current;
                    seenTopLeftBlue.Add(frame.Pixels.Span[0]); // top-left pixel's B channel, distinct per image
                    frame.Dispose();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // The sequence must have wrapped back to the first image by the
            // time it has cycled through more than N reads.
            Assert.Equal(seenTopLeftBlue[0], seenTopLeftBlue[imageCount]);
            Assert.Equal(seenTopLeftBlue[1], seenTopLeftBlue[imageCount + 1]);
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
            // frames without anyone ever calling ReadAsync.
            await Task.Delay(60, TestContext.Current.CancellationToken);

            await source.DisposeAsync();

            Assert.True(pool.RentCount > 0);
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
