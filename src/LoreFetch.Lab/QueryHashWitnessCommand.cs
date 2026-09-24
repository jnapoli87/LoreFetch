using LoreFetch.Core.Identification;
using LoreFetch.Lab.RoundTrip;

namespace LoreFetch.Lab;

/// `lab query-hash-witness --cache <dir> [--index <path>] --out <path>
///   [--sample-size N] [--seed N]`
///
/// Generates the committed query-hash witness
/// (`Tests/Lab/RoundTrip/round-trip-witness.json`) -- a fixed,
/// deterministic sample of query-side hashes, run by hand and committed as
/// derived (non-imagery) data, so a later run on a DIFFERENT architecture
/// can regenerate the same sample and diff the two. See
/// `QueryHashWitnessDocument`'s own doc comment for why this matters: it is
/// the only thing in this stream that actually MEASURES the query side's
/// own cross-architecture divergence, rather than assuming it from the
/// reference side's golden hashes (B1b).
public static class QueryHashWitnessCommand
{
    public const int DefaultSampleSize = 50;
    public const int DefaultSeed = 20260922;

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        QueryHashWitnessArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        if (!Directory.Exists(parsed.CacheDir))
        {
            Console.Error.WriteLine($"query-hash-witness: cache directory not found: \"{parsed.CacheDir}\".");
            return 1;
        }

        var indexPath = parsed.IndexPath;
        if (indexPath is null)
        {
            if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
            {
                Console.Error.WriteLine("query-hash-witness: could not locate the repository automatically. Pass --index explicitly.");
                return 1;
            }

            indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        }

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"query-hash-witness: index not found at \"{indexPath}\".");
            return 1;
        }

        HashIndexData index;
        try
        {
            index = HashIndexFile.Read(indexPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"query-hash-witness: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        var indexSha256 = HashIndexFile.ComputeSha256(indexPath);
        Console.WriteLine($"Index: {indexPath} ({index.Entries.Count} artworks)");
        Console.WriteLine($"Index SHA-256: {indexSha256}");
        Console.WriteLine($"Cache: {parsed.CacheDir}");
        Console.WriteLine($"Sample size: {parsed.SampleSize}, seed {parsed.Seed}");
        Console.WriteLine($"Measured on: {ArchitectureProvenance.CurrentDetail()}");

        QueryHashWitnessDocument document;
        try
        {
            document = QueryHashWitnessBuilder.Build(
                index, parsed.CacheDir, parsed.SampleSize, parsed.Seed, indexSha256, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"query-hash-witness: {ex.Message}");
            return 1;
        }

        QueryHashWitnessDocument.Write(parsed.OutPath, document);
        Console.WriteLine($"Wrote {parsed.OutPath} ({document.Entries.Count} entries)");
        return 0;
    }

    private sealed record QueryHashWitnessArgs(string CacheDir, string? IndexPath, string OutPath, int SampleSize, int Seed);

    private static QueryHashWitnessArgs ParseArgs(string[] args)
    {
        string? cacheDir = null;
        string? indexPath = null;
        string? outPath = null;
        var sampleSize = DefaultSampleSize;
        var seed = DefaultSeed;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cache" when i + 1 < args.Length:
                    cacheDir = args[++i];
                    break;
                case "--index" when i + 1 < args.Length:
                    indexPath = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--sample-size" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out sampleSize) || sampleSize <= 0)
                    {
                        throw new ArgumentException("query-hash-witness: --sample-size must be a positive integer.");
                    }

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("query-hash-witness: --seed must be an integer.");
                    }

                    break;
                default:
                    throw new ArgumentException($"query-hash-witness: unrecognised argument \"{args[i]}\".");
            }
        }

        if (cacheDir is null)
        {
            throw new ArgumentException("query-hash-witness: --cache <dir> is required.");
        }

        if (outPath is null)
        {
            throw new ArgumentException("query-hash-witness: --out <path> is required.");
        }

        return new QueryHashWitnessArgs(cacheDir, indexPath, outPath, sampleSize, seed);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              query-hash-witness --cache <dir> [--index <path>] --out <path>
                                  [--sample-size N] [--seed N]

            Samples --sample-size artworks (default 50) uniformly and
            deterministically from the committed index (default
            data/index/cards.lfidx), computes each one's query-side hash
            (QueryTransform.Prepare -> CardHasher.Hash, no orientation
            flip), and writes them -- sorted by ArtworkId -- to --out as
            derived (non-imagery) JSON. Fails if any sampled artwork's
            cache image is missing.
            """);
    }
}
