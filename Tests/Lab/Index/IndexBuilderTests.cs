using LoreFetch.Core.Identification;
using LoreFetch.Lab.Index;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Lab.Index;

/// `IndexBuilder` is the ONLY piece of `build-index` that touches pixels --
/// see its own doc comment -- so these tests are where B4c's core
/// invariants live: manifest order regardless of parallelism, the missing/
/// undecodable handling, and (most important) that the stored hash is
/// EXACTLY `CardHasher.Hash(ReferenceTransform.Prepare(image))` with no
/// third transform anywhere in between.
public sealed class IndexBuilderTests : IDisposable
{
    private readonly string _cacheDir = IndexBuildFixtures.NewTempCacheDir();

    public void Dispose() => IndexBuildFixtures.DeleteQuietly(_cacheDir);

    [Fact]
    public void Build_ProducesOneEntryPerManifestEntry_WithFieldsCarriedThrough()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 6);

        var result = IndexBuilder.Build(entries, new IndexBuildOptions { CacheDir = cacheDir, ProgressInterval = 0 });

        Assert.Equal(entries.Count, result.Entries.Count);
        Assert.Empty(result.MissingArtworkIds);

        for (var i = 0; i < entries.Count; i++)
        {
            var expected = entries[i];
            var actual = result.Entries[i];
            Assert.Equal(expected.ArtworkId, actual.ArtworkId);
            Assert.Equal(expected.OracleId, actual.OracleId);
            Assert.Equal(expected.OracleName, actual.OracleName);
            Assert.Equal(expected.IsBasicLand, actual.IsBasicLand);
        }
    }

    [Fact]
    public void Build_OrderMatchesManifestOrder_RegardlessOfParallelism()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 24);

        foreach (var parallelism in new[] { 1, 4, Environment.ProcessorCount })
        {
            var result = IndexBuilder.Build(
                entries, new IndexBuildOptions { CacheDir = cacheDir, Parallelism = parallelism, ProgressInterval = 0 });

            Assert.Equal(entries.Count, result.Entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                Assert.True(
                    entries[i].ArtworkId == result.Entries[i].ArtworkId,
                    $"parallelism={parallelism}: expected entry {i} to be {entries[i].ArtworkId}, " +
                    $"got {result.Entries[i].ArtworkId}.");
            }
        }
    }

    [Fact]
    public void Build_IsByteIdenticalAcrossParallelismSettings()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 20);

        var serial = IndexBuilder.Build(
            entries, new IndexBuildOptions { CacheDir = cacheDir, Parallelism = 1, ProgressInterval = 0 });
        var parallel = IndexBuilder.Build(
            entries, new IndexBuildOptions { CacheDir = cacheDir, Parallelism = 8, ProgressInterval = 0 });

        var outA = Path.Combine(cacheDir, "a.lfidx");
        var outB = Path.Combine(cacheDir, "b.lfidx");
        HashIndexFile.Write(outA, serial.Entries);
        HashIndexFile.Write(outB, parallel.Entries);

        Assert.Equal(HashIndexFile.ComputeSha256(outA), HashIndexFile.ComputeSha256(outB));
        Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
    }

    [Fact]
    public void Build_EachHashEqualsReferenceTransformThenCardHasher_ComputedDirectly()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 5);

        var result = IndexBuilder.Build(entries, new IndexBuildOptions { CacheDir = cacheDir, ProgressInterval = 0 });

        for (var i = 0; i < entries.Count; i++)
        {
            var imagePath = LoreFetch.Lab.Images.ImageCache.GetImagePath(cacheDir, entries[i].ArtworkId);
            using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
            using var gray = ReferenceTransform.Prepare(source);
            var expectedHash = CardHasher.Hash(gray);

            Assert.Equal(expectedHash, result.Entries[i].Hash);
        }
    }

    [Fact]
    public void Build_CountsButDoesNotFailOn_UnexpectedImageSize()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 8);
        // Fixture makes every 4th entry (index 3, 7, ...) a non-canonical size.
        var expectedUnexpected = entries.Count / 4;

        var result = IndexBuilder.Build(entries, new IndexBuildOptions { CacheDir = cacheDir, ProgressInterval = 0 });

        Assert.Equal(entries.Count, result.Entries.Count);
        Assert.Equal(expectedUnexpected, result.UnexpectedSizeCount);
    }

    [Fact]
    public void Build_MissingImage_IsReportedNotSilentlyDropped()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 5);
        var missingPath = LoreFetch.Lab.Images.ImageCache.GetImagePath(cacheDir, entries[2].ArtworkId);
        File.Delete(missingPath);

        var result = IndexBuilder.Build(entries, new IndexBuildOptions { CacheDir = cacheDir, ProgressInterval = 0 });

        Assert.Equal(entries.Count - 1, result.Entries.Count);
        Assert.Equal([entries[2].ArtworkId], result.MissingArtworkIds);
    }

    [Fact]
    public void Build_UndecodableImage_IsTreatedAsMissing()
    {
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 4);
        var corruptPath = LoreFetch.Lab.Images.ImageCache.GetImagePath(cacheDir, entries[1].ArtworkId);
        File.WriteAllBytes(corruptPath, [0xFF, 0xD8, 1, 2, 3]); // JPEG magic bytes, garbage body

        var result = IndexBuilder.Build(entries, new IndexBuildOptions { CacheDir = cacheDir, ProgressInterval = 0 });

        Assert.Equal(entries.Count - 1, result.Entries.Count);
        Assert.Contains(entries[1].ArtworkId, result.MissingArtworkIds);
    }

    [Fact]
    public void Build_DisposesEveryMat_NoLeakedNativeHandlesAcrossManyEntries()
    {
        // Not a leak detector -- there isn't a clean managed way to assert
        // "no OpenCvSharp Mat was left undisposed" from a unit test. This
        // instead exercises enough entries, at real parallelism, that a
        // genuine `using` omission (an un-disposed Mat per iteration) would
        // reliably exhaust native memory or throw well before this many
        // ran -- so a clean pass here is meaningful, if not a proof.
        var (entries, cacheDir) = IndexBuildFixtures.CreateCacheAndManifest(_cacheDir, count: 100);

        var result = IndexBuilder.Build(
            entries,
            new IndexBuildOptions { CacheDir = cacheDir, Parallelism = Environment.ProcessorCount, ProgressInterval = 0 });

        Assert.Equal(entries.Count, result.Entries.Count);
    }
}
