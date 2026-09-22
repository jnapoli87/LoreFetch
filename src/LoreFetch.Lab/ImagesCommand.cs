using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Images;

namespace LoreFetch.Lab;

/// `lab images [--manifest <path>] --cache <dir> [--concurrency N] [--limit N]`
/// -- downloads B4a's manifest entries' `image_uris.normal` renders into an
/// external cache, keyed by `ArtworkId` (see `ImageCache`), for B4c
/// (`build-index`) to read.
public static class ImagesCommand
{
    public static async Task<int> RunAsync(string[] args, HttpClient? httpClient = null)
    {
        string? cacheDir;
        string? manifestPath;
        int concurrency;
        int? limit;

        try
        {
            (cacheDir, manifestPath, concurrency, limit) = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (cacheDir is null)
        {
            Console.Error.WriteLine("images: --cache <dir> is required.");
            return 1;
        }

        var repoRoot = RepoPaths.FindRepoRoot();

        try
        {
            CacheDirectoryGuard.EnsureOutsideRepo(cacheDir, repoRoot);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        manifestPath ??= Path.Combine(RepoPaths.DefaultScryfallBulkDir(), BulkCommand.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine(
                $"images: manifest not found at \"{manifestPath}\". Run \"lab bulk\" first, " +
                "or pass --manifest explicitly.");
            return 1;
        }

        var entries = FilteredArtworkManifest.Read(manifestPath);
        Console.WriteLine($"Manifest: {manifestPath} ({entries.Count} entries)");
        Console.WriteLine($"Cache: {cacheDir}");
        if (limit is int lim)
        {
            Console.WriteLine($"Limit: first {lim} entries");
        }

        Directory.CreateDirectory(cacheDir);

        using var http = httpClient ?? ImageDownloader.CreateHttpClient();
        var downloader = new ImageDownloader(http);

        var options = new ImageDownloadOptions
        {
            CacheDir = cacheDir,
            Concurrency = concurrency,
            Limit = limit,
        };

        var summary = await downloader.RunAsync(entries, options, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine(
            $"Done: {summary.Done} downloaded, {summary.Skipped} skipped, {summary.Failed} failed " +
            $"({summary.Total} total) in {summary.Elapsed.TotalSeconds:F1}s " +
            $"({(summary.Elapsed.TotalSeconds > 0 ? summary.Total / summary.Elapsed.TotalSeconds : 0):F1}/s).");

        if (summary.UnexpectedSizeCount > 0)
        {
            Console.WriteLine($"{summary.UnexpectedSizeCount} downloads were not the expected size.");
        }

        if (summary.FailedIds.Count > 0)
        {
            Console.WriteLine($"Failed artwork ids ({summary.FailedIds.Count}):");
            foreach (var id in summary.FailedIds)
            {
                Console.WriteLine($"  {id}");
            }
        }

        return summary.Failed > 0 ? 1 : 0;
    }

    private static (string? CacheDir, string? ManifestPath, int Concurrency, int? Limit) ParseArgs(string[] args)
    {
        string? cacheDir = null;
        string? manifestPath = null;
        var concurrency = 4;
        int? limit = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cache" when i + 1 < args.Length:
                    cacheDir = args[++i];
                    break;
                case "--manifest" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--concurrency" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out concurrency) || concurrency < 1)
                    {
                        throw new ArgumentException("images: --concurrency must be a positive integer.");
                    }

                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedLimit) || parsedLimit < 0)
                    {
                        throw new ArgumentException("images: --limit must be a non-negative integer.");
                    }

                    limit = parsedLimit;
                    break;
                default:
                    throw new ArgumentException($"images: unrecognised argument \"{args[i]}\".");
            }
        }

        return (cacheDir, manifestPath, concurrency, limit);
    }
}
