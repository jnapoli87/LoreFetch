using System.IO.Compression;
using System.Text.Json;
using LoreFetch.Lab.Bulk;
using Xunit;

namespace LoreFetch.Tests.Lab.Bulk;

/// `ScryfallJsonl` must read a real `*.jsonl.gz` download and a plain
/// committed `.jsonl` fixture identically, since `bulk`/`printings`
/// exercise the same code path on both. No network access -- the gzip
/// file is produced locally from the same fixture used elsewhere.
public class ScryfallJsonlTests
{
    [Fact]
    public void ReadLines_GzippedFile_ParsesTheSameRecordsAsThePlainFile()
    {
        var plainPath = BulkFixtures.UniqueArtworkSamplePath();
        var gzPath = MakeGzipCopy(plainPath);
        try
        {
            var fromPlain = ScryfallJsonl.ReadLines(plainPath).Select(RawArtwork.Parse).ToList();
            var fromGzip = ScryfallJsonl.ReadLines(gzPath).Select(RawArtwork.Parse).ToList();

            Assert.Equal(fromPlain.Count, fromGzip.Count);
            Assert.True(fromPlain.Count > 0); // guard against a vacuous empty-vs-empty pass

            for (var i = 0; i < fromPlain.Count; i++)
            {
                Assert.Equal(fromPlain[i], fromGzip[i]);
            }
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    [Fact]
    public void ReadLines_SkipsBlankLines()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"id\":\"a\"}\n\n{\"id\":\"b\"}\n");

            var elements = ScryfallJsonl.ReadLines(path).ToList();

            Assert.Equal(2, elements.Count);
            Assert.Equal("a", elements[0].GetProperty("id").GetString());
            Assert.Equal("b", elements[1].GetProperty("id").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string MakeGzipCopy(string plainPath)
    {
        var gzPath = TempPath() + ".gz";
        using var input = File.OpenRead(plainPath);
        using var output = File.Create(gzPath);
        using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        input.CopyTo(gzip);
        return gzPath;
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lorefetch-jsonl-test-{Guid.NewGuid():N}");
}
