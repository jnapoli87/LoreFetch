using LoreFetch.Core.Identification;
using LoreFetch.Lab.CropScale;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Lab;

/// `lab crop-scale --cache <dir> [--index <path>] [--sample N] [--seed N]`
///
/// Package B5c's crop-scale experiment: measures how `ICardIdentifier`'s
/// rank-1 rate and its own-distance/margin distributions degrade as a
/// synthetic crop-scale error (`CropScaleTransform`) is applied to real
/// Scryfall renders before they are presented through the shipping query
/// path -- see `CropScaleExperimentRunner`'s own doc comment for the
/// mechanism, and CLAUDE.md/orchestration-plan.md's B5c item for why this
/// exists (B5b's `plains_black` failure: a black-bordered card's outer
/// edge merges into a dark mat, so the detected quad is inset from the
/// true card, and the result is a CONFIDENT WRONG match, not a miss).
///
/// This is a maintainer-run measurement tool, same category as
/// `round-trip-gate` -- it never writes to any committed file itself; its
/// output is transcribed into `docs/accuracy.md` by hand.
public static class CropScaleCommand
{
    public const int DefaultSampleSize = 150;
    public const int DefaultSeed = 20260922;

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        CropScaleArgs parsed;
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
            Console.Error.WriteLine($"crop-scale: cache directory not found: \"{parsed.CacheDir}\".");
            return 1;
        }

        var indexPath = parsed.IndexPath;
        if (indexPath is null)
        {
            if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
            {
                Console.Error.WriteLine("crop-scale: could not locate the repository automatically. Pass --index explicitly.");
                return 1;
            }

            indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        }

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"crop-scale: index not found at \"{indexPath}\".");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddSimpleConsole(o => o.SingleLine = true));

        HashIndexData index;
        HashCardIdentifier identifier;
        try
        {
            index = HashIndexFile.Read(indexPath);
            identifier = HashCardIdentifier.Load(indexPath, loggerFactory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crop-scale: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Index: {indexPath} ({index.Entries.Count} artworks, {index.OracleTable.Count} oracle cards)");
        Console.WriteLine($"Cache: {parsed.CacheDir}");
        Console.WriteLine($"Sample: {parsed.SampleSize} non-land artworks, seed {parsed.Seed}");
        Console.WriteLine();

        var options = new CropScaleExperimentOptions
        {
            CacheDir = parsed.CacheDir,
            SampleSize = parsed.SampleSize,
            Seed = parsed.Seed,
        };

        CropScaleExperimentSummary summary;
        try
        {
            summary = CropScaleExperimentRunner.Run(index, identifier, options);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"crop-scale: {ex.Message}");
            return 1;
        }

        Console.WriteLine(
            $"{"Level",-26} {"Rank1%",7} {"OwnMin",7} {"OwnMean",8} {"OwnMed",7} {"OwnMax",7} {"MgMin",7} {"MgMean",8} {"MgMed",7} {"MgMax",7}");
        foreach (var point in summary.Points)
        {
            var s = point.Statistics;
            Console.WriteLine(
                $"{point.Label,-26} {s.Rank1Rate,7:P1} {s.OwnDistanceMin,7} {s.OwnDistanceMean,8:F1} {s.OwnDistanceMedian,7} " +
                $"{s.OwnDistanceMax,7} {s.MarginMin,7} {s.MarginMean,8:F1} {s.MarginMedian,7} {s.MarginMax,7}");
        }

        return 0;
    }

    private sealed record CropScaleArgs(string CacheDir, string? IndexPath, int SampleSize, int Seed);

    private static CropScaleArgs ParseArgs(string[] args)
    {
        string? cacheDir = null;
        string? indexPath = null;
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
                case "--sample" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out sampleSize) || sampleSize < 1)
                    {
                        throw new ArgumentException("crop-scale: --sample must be a positive integer.");
                    }

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("crop-scale: --seed must be an integer.");
                    }

                    break;
                default:
                    throw new ArgumentException($"crop-scale: unrecognised argument \"{args[i]}\".");
            }
        }

        if (cacheDir is null)
        {
            throw new ArgumentException("crop-scale: --cache <dir> is required.");
        }

        return new CropScaleArgs(cacheDir, indexPath, sampleSize, seed);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              crop-scale --cache <dir> [--index <path>] [--sample N] [--seed N]

            Package B5c: samples --sample (default 150) non-land artworks
            deterministically from the committed index, applies a bracket
            of synthetic crop-scale errors (CropScaleTransform) to each
            cached render, and reports the rank-1 ArtworkId rate plus the
            own-distance/margin distributions per level -- the curve behind
            docs/accuracy.md's crop-scale table.
            """);
    }
}
