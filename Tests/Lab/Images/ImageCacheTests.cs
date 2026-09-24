using LoreFetch.Lab.Images;
using Xunit;

namespace LoreFetch.Tests.Lab.Images;

/// The path mapping is the seam B4c reuses to find a cached artwork
/// deterministically -- these tests pin its exact shape.
public class ImageCacheTests
{
    [Fact]
    public void GetImagePath_CombinesCacheDirAndArtworkIdWithJpgExtension()
    {
        var path = ImageCache.GetImagePath(Path.Combine("C:", "cache"), "abc-123");

        Assert.Equal(Path.Combine("C:", "cache", "abc-123.jpg"), path);
    }

    [Fact]
    public void GetTempPath_IsTheImagePathWithADownloadSuffix()
    {
        var imagePath = ImageCache.GetImagePath(Path.Combine("C:", "cache"), "abc-123");
        var tempPath = ImageCache.GetTempPath(Path.Combine("C:", "cache"), "abc-123");

        Assert.Equal(imagePath + ".download", tempPath);
    }

    [Fact]
    public void DifferentArtworkIds_NeverCollide()
    {
        var a = ImageCache.GetImagePath(Path.Combine("C:", "cache"), "abc-123");
        var b = ImageCache.GetImagePath(Path.Combine("C:", "cache"), "abc-124");

        Assert.NotEqual(a, b);
    }
}
