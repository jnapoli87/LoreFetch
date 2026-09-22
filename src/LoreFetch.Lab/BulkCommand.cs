using LoreFetch.Lab.Bulk;

namespace LoreFetch.Lab;

/// `lab bulk [--out <dir>]` -- downloads `unique_artwork`, runs the filter
/// cascade, prints it, and writes the surviving arts to a manifest that
/// B4b (image download) and B4c (index build) consume.
public static class BulkCommand
{
    public const string BulkType = "unique_artwork";
    public const string ManifestFileName = "filtered-artworks.jsonl";

    public static async Task<int> RunAsync(string[] args, HttpClient? httpClient = null)
    {
        string outDir;
        try
        {
            outDir = ParseOutDir(args) ?? RepoPaths.DefaultScryfallBulkDir();
        }
        catch (RepoRootNotFoundException ex)
        {
            Console.Error.WriteLine($"bulk: {ex.Message} Pass --out explicitly.");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        using var http = httpClient ?? ScryfallBulkClient.CreateHttpClient();
        var client = new ScryfallBulkClient(http);

        Console.WriteLine($"Resolving {BulkType} bulk data...");
        var bulkPath = await client.DownloadBulkFileAsync(BulkType, outDir, CancellationToken.None);
        Console.WriteLine($"Bulk file: {bulkPath}");
        Console.WriteLine();

        var records = ScryfallJsonl.ReadLines(bulkPath).Select(RawArtwork.Parse);
        var result = ArtworkFilterCascade.Run(records);

        PrintCascade(result);

        var manifestPath = Path.Combine(outDir, ManifestFileName);
        var manifestEntries = result.Survivors.Select(ManifestEntry.FromArtwork).ToList();
        FilteredArtworkManifest.Write(manifestPath, manifestEntries);
        Console.WriteLine();
        Console.WriteLine($"Wrote manifest: {manifestPath} ({manifestEntries.Count} entries)");

        return 0;
    }

    private static void PrintCascade(ArtworkCascadeResult result)
    {
        foreach (var step in result.Steps)
        {
            Console.WriteLine($"{step.Name}: {step.ArtCount} arts / {step.DistinctOracleIdCount} oracle ids");

            if (step.Name == ArtworkFilterCascade.HasImageUrisStepName)
            {
                Console.WriteLine(
                    $"  (explicitly skipped {result.SkippedNoImageUrisCount} multi-faced objects " +
                    "with no top-level image_uris)");
            }
        }

        Console.WriteLine(
            $"Informational only, NOT applied -- frame == \"2015\": " +
            $"{result.InformationalFrame2015Count} of {result.FinalStep.ArtCount}");
    }

    internal static string? ParseOutDir(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
