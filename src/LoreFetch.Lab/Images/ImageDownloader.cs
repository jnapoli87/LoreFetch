using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using LoreFetch.Lab.Bulk;
using OpenCvSharp;

namespace LoreFetch.Lab.Images;

/// One entry's outcome, for the run summary and the failed-id list.
public enum ImageDownloadStatus
{
    Downloaded,
    Skipped,
    Failed,
}

public sealed record ImageDownloadOutcome(string ArtworkId, ImageDownloadStatus Status, string? Reason, bool UnexpectedSize);

public sealed record ImageDownloadSummary(
    int Done,
    int Skipped,
    int Failed,
    IReadOnlyList<string> FailedIds,
    int UnexpectedSizeCount,
    TimeSpan Elapsed)
{
    public int Total => Done + Skipped + Failed;
}

public sealed class ImageDownloadOptions
{
    public required string CacheDir { get; init; }
    public int Concurrency { get; init; } = 4;
    public int? Limit { get; init; }
    public int MaxRetries { get; init; } = 5;
    public TimeSpan MinimumRetryAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public int ProgressInterval { get; init; } = 100;

    /// Cheap OpenCvSharp decode of each finished download to log a count
    /// of images that aren't the expected 488x680 `normal` size. Never
    /// fails the download on its own -- see stream-b-identification.md B4.
    public bool CheckImageSize { get; init; } = true;
    public int ExpectedWidth { get; init; } = 488;
    public int ExpectedHeight { get; init; } = 680;
}

/// Downloads `image_uris.normal` renders into an external, gitignored cache,
/// keyed by `ArtworkId` via `ImageCache`. Resumable (skips files already
/// present), polite (bounded concurrency, honours 429), and never leaves a
/// truncated `<id>.jpg` behind -- every download lands in a temp file first
/// and is only moved into place after it validates.
///
/// The HTTP transport and the retry/429 delay are both injectable so the
/// whole loop can be driven from a test with no network and no real waits.
public sealed class ImageDownloader
{
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ImageDownloader(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    /// A ready-to-use client with the mandatory Scryfall headers set
    /// explicitly, never left to the library default. Accept is `image/*`
    /// with a `*/*` fallback -- these are image downloads, not the JSON
    /// API, so `ScryfallBulkClient`'s `application/json` Accept doesn't fit,
    /// but the User-Agent constant is shared rather than duplicated.
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ScryfallBulkClient.UserAgent);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        return client;
    }

    public async Task<ImageDownloadSummary> RunAsync(
        IReadOnlyList<ManifestEntry> entries,
        ImageDownloadOptions options,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.CacheDir);

        var work = options.Limit is int limit ? entries.Take(limit).ToList() : entries;

        var done = 0;
        var skipped = 0;
        var failed = 0;
        var unexpectedSize = 0;
        var failedIds = new ConcurrentBag<string>();
        var completed = 0;
        var started = DateTime.UtcNow;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(work, parallelOptions, async (entry, itemCt) =>
        {
            var outcome = await DownloadOneAsync(entry, options, itemCt);

            switch (outcome.Status)
            {
                case ImageDownloadStatus.Downloaded:
                    Interlocked.Increment(ref done);
                    break;
                case ImageDownloadStatus.Skipped:
                    Interlocked.Increment(ref skipped);
                    break;
                case ImageDownloadStatus.Failed:
                    Interlocked.Increment(ref failed);
                    failedIds.Add(outcome.ArtworkId);
                    Console.WriteLine($"FAILED {outcome.ArtworkId}: {outcome.Reason}");
                    break;
            }

            if (outcome.UnexpectedSize)
            {
                Interlocked.Increment(ref unexpectedSize);
            }

            var completedNow = Interlocked.Increment(ref completed);
            if (options.ProgressInterval > 0 && completedNow % options.ProgressInterval == 0)
            {
                ReportProgress(completedNow, work.Count, done, skipped, failed, started);
            }
        });

        var elapsed = DateTime.UtcNow - started;
        return new ImageDownloadSummary(done, skipped, failed, failedIds.ToList(), unexpectedSize, elapsed);
    }

    private static void ReportProgress(int completed, int total, int done, int skipped, int failed, DateTime started)
    {
        var elapsed = DateTime.UtcNow - started;
        var rate = elapsed.TotalSeconds > 0 ? completed / elapsed.TotalSeconds : 0;
        Console.WriteLine(
            $"progress: {completed}/{total} (done={done} skipped={skipped} failed={failed} " +
            $"remaining={total - completed}) -- {rate:F1}/s");
    }

    /// Downloads one entry, or determines it needs no download. Never
    /// throws on a per-entry failure -- every path returns an outcome so
    /// one bad artwork never aborts the run.
    private async Task<ImageDownloadOutcome> DownloadOneAsync(
        ManifestEntry entry,
        ImageDownloadOptions options,
        CancellationToken ct)
    {
        // Checked before any request is sent -- the manifest should never
        // carry a non-`normal` URL, but this is the last line of defence
        // against silently hashing a `small` render into the index (see
        // CLAUDE.md's "Pull normal, not small").
        if (!entry.ImageUriNormal.Contains("/normal/", StringComparison.Ordinal))
        {
            return new ImageDownloadOutcome(
                entry.ArtworkId,
                ImageDownloadStatus.Failed,
                $"refusing non-normal image URL: {entry.ImageUriNormal}",
                UnexpectedSize: false);
        }

        var finalPath = ImageCache.GetImagePath(options.CacheDir, entry.ArtworkId);
        if (File.Exists(finalPath))
        {
            return new ImageDownloadOutcome(entry.ArtworkId, ImageDownloadStatus.Skipped, null, UnexpectedSize: false);
        }

        var tempPath = ImageCache.GetTempPath(options.CacheDir, entry.ArtworkId);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await SendWithRetryOn429Async(entry.ImageUriNormal, options, ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < options.MaxRetries)
                    {
                        await BackoffAsync(attempt, options, ct);
                        continue;
                    }

                    return new ImageDownloadOutcome(
                        entry.ArtworkId,
                        ImageDownloadStatus.Failed,
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                        UnexpectedSize: false);
                }

                // Temp file, in the cache directory itself, then move into
                // place -- so a killed run never leaves a truncated
                // <id>.jpg that a later run would mistake for "already
                // have it" and skip. File.Create truncates any leftover
                // temp file from a previous killed run, so nothing needs
                // explicit cleanup before this.
                await using (var fileStream = File.Create(tempPath))
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                {
                    await contentStream.CopyToAsync(fileStream, ct);
                }

                var (ok, reason, unexpectedSize) = ValidateDownloadedFile(tempPath, options);
                if (!ok)
                {
                    TryDelete(tempPath);
                    if (attempt < options.MaxRetries)
                    {
                        await BackoffAsync(attempt, options, ct);
                        continue;
                    }

                    return new ImageDownloadOutcome(entry.ArtworkId, ImageDownloadStatus.Failed, reason, UnexpectedSize: false);
                }

                File.Move(tempPath, finalPath, overwrite: true);
                return new ImageDownloadOutcome(entry.ArtworkId, ImageDownloadStatus.Downloaded, null, unexpectedSize);
            }
            catch (Exception ex) when (IsTransientException(ex, ct))
            {
                TryDelete(tempPath);
                if (attempt < options.MaxRetries)
                {
                    await BackoffAsync(attempt, options, ct);
                    continue;
                }

                return new ImageDownloadOutcome(entry.ArtworkId, ImageDownloadStatus.Failed, ex.Message, UnexpectedSize: false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) => (int)status is >= 500 and < 600;

    private static bool IsTransientException(Exception ex, CancellationToken ct) =>
        ex switch
        {
            IOException => true,
            HttpRequestException => true,
            TaskCanceledException when !ct.IsCancellationRequested => true, // a timeout, not a real cancel
            _ => false,
        };

    private Task BackoffAsync(int attempt, ImageDownloadOptions options, CancellationToken ct)
    {
        var delay = TimeSpan.FromTicks(options.InitialBackoff.Ticks * (1L << Math.Min(attempt, 10)));
        return _delay(delay, ct);
    }

    /// Scryfall: "It is not acceptable to ignore HTTP 429 responses." Waits
    /// for `Retry-After` when present, otherwise the documented 30 s
    /// lockout, and never less than the documented minimum even if a
    /// server sent a smaller value. Retries indefinitely on 429 rather
    /// than counting it against `MaxRetries` -- a 429 is not the entry's
    /// fault, it means the whole run is going too fast.
    private async Task<HttpResponseMessage> SendWithRetryOn429Async(
        string uri,
        ImageDownloadOptions options,
        CancellationToken ct)
    {
        while (true)
        {
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? options.MinimumRetryAfter;
            if (wait < options.MinimumRetryAfter)
            {
                wait = options.MinimumRetryAfter;
            }

            response.Dispose();
            Console.WriteLine($"429 for {uri} -- waiting {wait.TotalSeconds:F0}s before retry.");
            await _delay(wait, ct);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only -- a leftover temp file is
            // harmless; File.Create overwrites it on the next attempt.
        }
    }

    /// Non-empty, starts with the JPEG magic bytes (FF D8), and --
    /// cheaply, via OpenCvSharp -- decodes to the expected 488x680. The
    /// size check is diagnostic only: a decode failure or an unexpected
    /// size is counted, never treated as a download failure, per
    /// stream-b-identification.md B4 ("at least log a count of unexpected
    /// sizes rather than failing").
    private static (bool Ok, string? Reason, bool UnexpectedSize) ValidateDownloadedFile(
        string path,
        ImageDownloadOptions options)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return (false, "empty download", false);
        }

        Span<byte> header = stackalloc byte[2];
        using (var stream = File.OpenRead(path))
        {
            var read = stream.Read(header);
            if (read < 2 || header[0] != 0xFF || header[1] != 0xD8)
            {
                return (false, "not a JPEG (missing FF D8 magic bytes)", false);
            }
        }

        if (!options.CheckImageSize)
        {
            return (true, null, false);
        }

        var unexpectedSize = false;
        try
        {
            using var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
            if (mat.Empty() || mat.Width != options.ExpectedWidth || mat.Height != options.ExpectedHeight)
            {
                unexpectedSize = true;
            }
        }
        catch
        {
            // Non-fatal: the magic-byte check already passed, so this is
            // treated as "couldn't confirm the size," not "bad download."
            unexpectedSize = true;
        }

        return (true, null, unexpectedSize);
    }
}
