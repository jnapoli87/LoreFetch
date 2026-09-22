using System.Net;
using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Images;
using Xunit;

namespace LoreFetch.Tests.StreamB.Images;

/// `ImageDownloader` is the B4b download loop: no real network, an
/// injectable `HttpMessageHandler`, and an injectable delay so 429/backoff
/// waits are asserted rather than actually slept through.
public sealed class ImageDownloaderTests : IDisposable
{
    private readonly string _cacheDir;

    public ImageDownloaderTests()
    {
        _cacheDir = Directory.CreateTempSubdirectory("lorefetch-images-test-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static ManifestEntry MakeEntry(string artworkId, string imageUriNormal = "") => new(
        ArtworkId: artworkId,
        OracleId: "oracle-" + artworkId,
        OracleName: "Test Card " + artworkId,
        TypeLine: "Creature — Test",
        ImageUriNormal: imageUriNormal.Length == 0
            ? $"https://cards.scryfall.io/normal/front/a/a/{artworkId}.jpg"
            : imageUriNormal,
        IsBasicLand: false);

    private static HttpResponseMessage JpegResponse(byte[]? body = null) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body ?? [0xFF, 0xD8, 0x01, 0x02, 0x03, 0xFF, 0xD9]),
    };

    [Fact]
    public async Task SkipsFilesAlreadyPresent_WithoutMakingAnyRequest()
    {
        var entry = MakeEntry("skip1");
        var finalPath = ImageCache.GetImagePath(_cacheDir, entry.ArtworkId);
        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllBytesAsync(finalPath, [0xFF, 0xD8, 0x00], TestContext.Current.CancellationToken);

        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("a present file must never trigger a request"));
        var downloader = new ImageDownloader(new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, CheckImageSize = false };

        var summary = await downloader.RunAsync([entry], options, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Done);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FailureMidDownload_LeavesNoFinalFileAndNoLeftoverTemp()
    {
        var entry = MakeEntry("fault1");
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            // Delivers only the JPEG magic bytes, then the stream throws --
            // simulating a connection dropping mid-transfer.
            var content = new StreamContent(new FaultyStream([0xFF, 0xD8, 0x01, 0x02, 0x03], failAfterBytes: 2));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var downloader = new ImageDownloader(new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, MaxRetries = 0, CheckImageSize = false };

        var summary = await downloader.RunAsync([entry], options, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Contains(entry.ArtworkId, summary.FailedIds);
        Assert.False(File.Exists(ImageCache.GetImagePath(_cacheDir, entry.ArtworkId)),
            "a mid-download failure must never leave a truncated <id>.jpg");
        Assert.False(File.Exists(ImageCache.GetTempPath(_cacheDir, entry.ArtworkId)),
            "the leftover temp file must be cleaned up after a permanent failure");
    }

    [Fact]
    public async Task TooManyRequests_WaitsAtLeastTheMinimumThenSucceeds()
    {
        var entry = MakeEntry("throttled1");
        var requestNumber = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            requestNumber++;
            if (requestNumber == 1)
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            }

            return Task.FromResult(JpegResponse());
        });

        var recordedWaits = new List<TimeSpan>();
        var downloader = new ImageDownloader(new HttpClient(handler), delay: (wait, _) =>
        {
            recordedWaits.Add(wait);
            return Task.CompletedTask; // injected: the test never actually sleeps
        });
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, CheckImageSize = false };

        var summary = await downloader.RunAsync([entry], options, CancellationToken.None);

        Assert.Equal(1, summary.Done);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, handler.RequestCount);
        Assert.Single(recordedWaits);
        Assert.True(recordedWaits[0] >= TimeSpan.FromSeconds(30),
            $"429 wait must be at least 30s, was {recordedWaits[0]}");
    }

    [Fact]
    public async Task PermanentFailure_IsCountedAndTheRunContinues()
    {
        var goodEntry = MakeEntry("good1");
        var badEntry = MakeEntry("bad1");

        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.ToString().Contains("bad1"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(JpegResponse());
        });

        var downloader = new ImageDownloader(new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, MaxRetries = 1, CheckImageSize = false };

        var summary = await downloader.RunAsync([goodEntry, badEntry], options, CancellationToken.None);

        Assert.Equal(1, summary.Done);
        Assert.Equal(1, summary.Failed);
        Assert.Equal([badEntry.ArtworkId], summary.FailedIds);
        Assert.True(File.Exists(ImageCache.GetImagePath(_cacheDir, goodEntry.ArtworkId)));
        Assert.False(File.Exists(ImageCache.GetImagePath(_cacheDir, badEntry.ArtworkId)));
    }

    [Fact]
    public async Task RefusesAUrlThatIsNotTheNormalSize_WithoutMakingARequest()
    {
        var entry = MakeEntry("smallurl1", imageUriNormal: "https://cards.scryfall.io/small/front/a/a/smallurl1.jpg");
        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("a non-normal URL must never be requested"));
        var downloader = new ImageDownloader(new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, CheckImageSize = false };

        var summary = await downloader.RunAsync([entry], options, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Contains(entry.ArtworkId, summary.FailedIds);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LimitDownloadsOnlyTheFirstNEntries()
    {
        var entries = Enumerable.Range(0, 5).Select(i => MakeEntry($"lim{i}")).ToList();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JpegResponse()));
        var downloader = new ImageDownloader(new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var options = new ImageDownloadOptions { CacheDir = _cacheDir, Limit = 2, CheckImageSize = false };

        var summary = await downloader.RunAsync(entries, options, CancellationToken.None);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, handler.RequestCount);
    }
}
