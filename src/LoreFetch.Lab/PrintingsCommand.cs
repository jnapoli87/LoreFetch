using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Printings;

namespace LoreFetch.Lab;

/// `lab printings [--out <dir>]` -- the open question deferred from the
/// `ArtworkId` ruling: what fraction of in-scope artworks have exactly
/// one in-scope printing? Streams `default_cards` (large -- default_cards
/// is every printing, not just one art per card) rather than loading it
/// whole.
public static class PrintingsCommand
{
    public const string BulkType = "default_cards";

    public static async Task<int> RunAsync(string[] args, HttpClient? httpClient = null)
    {
        string outDir;
        try
        {
            outDir = BulkCommand.ParseOutDir(args) ?? RepoPaths.DefaultScryfallBulkDir();
        }
        catch (RepoRootNotFoundException ex)
        {
            Console.Error.WriteLine($"printings: {ex.Message} Pass --out explicitly.");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        using var http = httpClient ?? ScryfallBulkClient.CreateHttpClient();
        var client = new ScryfallBulkClient(http);

        Console.WriteLine($"Resolving {BulkType} bulk data...");
        var bulkPath = await client.DownloadBulkFileAsync(BulkType, outDir, CancellationToken.None);
        Console.WriteLine($"Bulk file: {bulkPath}");
        Console.WriteLine();

        var printings = ScryfallJsonl.ReadLines(bulkPath).Select(RawPrinting.Parse);
        var result = PrintingsAnalyzer.Analyze(printings);

        Console.WriteLine($"Filter: {PrintingsAnalyzer.FilterDescription}");
        Console.WriteLine($"In-scope printings: {result.InScopePrintingCount}");
        Console.WriteLine($"Distinct illustration_ids (in-scope artworks): {result.DistinctIllustrationCount}");
        Console.WriteLine($"...with exactly one in-scope printing: {result.SingletonIllustrationCount}");
        Console.WriteLine($"Fraction: {result.SingletonFraction:P2}");

        return 0;
    }
}
