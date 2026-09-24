using LoreFetch.Core.Identification;
using LoreFetch.Lab.RoundTrip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoreFetch.Lab;

/// `lab round-trip-gate --cache <dir> [--index <path>] [--out <thresholds.json>]
///   [--lands N] [--non-lands N] [--seed N] [--min-rank1-rate F]`
///
/// The maintainer-run tool that actually PRODUCES the committed
/// `data/index/thresholds.json` (`referenceFloor` and the margin
/// statistics) -- package B2, "the one gate that matters most"
/// (CLAUDE.md). This command is what an implementer runs BY HAND, once,
/// against the real Scryfall image cache and the real committed index, and
/// commits the result; it deliberately does NOT run as part of the normal
/// test suite (`Tests/StreamB/RoundTrip/RoundTripGateTests.cs` performs the
/// SAME measurement every real run, for verification, but never writes to
/// the committed file itself -- see that test's own doc comment for why).
/// This mirrors B4d's own precedent: the committed index is likewise built
/// by a deliberate, human-run command, not by CI.
public static class RoundTripGateCommand
{
    public const int DefaultLandSampleSize = 20;
    public const int DefaultNonLandSampleSize = 180;
    public const int DefaultSeed = 20260922;
    public const double DefaultMinRank1Rate = 0.99; // CLAUDE.md / docs/history/orchestration-plan.md B2: "stop and ask" below this

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        RoundTripGateArgs parsed;
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
            Console.Error.WriteLine($"round-trip-gate: cache directory not found: \"{parsed.CacheDir}\".");
            return 1;
        }

        var indexPath = parsed.IndexPath;
        if (indexPath is null)
        {
            if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
            {
                Console.Error.WriteLine("round-trip-gate: could not locate the repository automatically. Pass --index explicitly.");
                return 1;
            }

            indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        }

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"round-trip-gate: index not found at \"{indexPath}\".");
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
            Console.Error.WriteLine($"round-trip-gate: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        var indexSha256 = HashIndexFile.ComputeSha256(indexPath);
        Console.WriteLine($"Index: {indexPath} ({index.Entries.Count} artworks, {index.OracleTable.Count} oracle cards)");
        Console.WriteLine($"Index SHA-256: {indexSha256}");
        Console.WriteLine($"Cache: {parsed.CacheDir}");
        Console.WriteLine($"Sample: {parsed.LandCount} lands + {parsed.NonLandCount} non-lands, seed {parsed.Seed}");
        Console.WriteLine($"Measured on: {ArchitectureProvenance.CurrentDetail()}");
        Console.WriteLine();

        var options = new RoundTripGateOptions
        {
            CacheDir = parsed.CacheDir,
            LandSampleSize = parsed.LandCount,
            NonLandSampleSize = parsed.NonLandCount,
            Seed = parsed.Seed,
        };

        var summary = RoundTripGateRunner.Run(index, identifier, options);
        var stats = RoundTripGateStatistics.From(summary);

        PrintReport(stats);

        if (stats.MissingCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"WARNING: {stats.MissingCount} sampled artwork(s) had no usable cache image: " +
                string.Join(", ", stats.MissingArtworkIds.Take(10)) +
                (stats.MissingArtworkIds.Count > 10 ? ", ..." : string.Empty));
        }

        if (stats.Rank1Rate < parsed.MinRank1Rate)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"STOP: rank-1 ArtworkId match rate {stats.Rank1Rate:P2} is below the {parsed.MinRank1Rate:P0} floor. " +
                "Per CLAUDE.md/orchestration-plan.md B2, this is a project-level stop-and-ask condition -- " +
                "the user decides what happens next, not this tool. Failures:");
            foreach (var failure in stats.Failures)
            {
                Console.WriteLine(
                    $"  {failure.ArtworkId} (oracle {failure.OracleId}, land={failure.IsBasicLand}): " +
                    $"own distance {failure.OwnDistance}, rank1 -> {failure.Rank1ArtworkId ?? "(none)"} " +
                    $"at distance {failure.Rank1Distance}.");
            }

            if (!parsed.Force)
            {
                Console.WriteLine();
                Console.WriteLine("Not writing thresholds.json. Pass --force to write anyway.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("--force given: writing thresholds.json despite the rate being below floor.");
        }

        if (parsed.OutPath is not null)
        {
            var document = RoundTripThresholdsDocument.FromStatistics(stats, indexSha256, index.Entries.Count, DateTimeOffset.UtcNow);
            RoundTripThresholdsWriter.Write(parsed.OutPath, document, parsed.Seed);
            Console.WriteLine();
            Console.WriteLine($"Wrote {parsed.OutPath}");
        }

        return 0;
    }

    private static void PrintReport(RoundTripGateStatistics stats)
    {
        Console.WriteLine($"Sample size: {stats.SampleSize} ({stats.AvailableCount} available, {stats.MissingCount} missing image)");
        Console.WriteLine(
            $"Rank-1 ArtworkId match rate: {stats.Rank1Rate:P2} overall ({stats.CorrectCount}/{stats.AvailableCount})");
        Console.WriteLine(
            $"  lands:     {stats.LandRank1Rate:P2} ({stats.LandCorrectCount}/{stats.LandSampleSize})");
        Console.WriteLine(
            $"  non-lands: {stats.NonLandRank1Rate:P2} ({stats.NonLandCorrectCount}/{stats.NonLandSampleSize})");
        Console.WriteLine(
            $"Own distance (correct matches only): min {stats.OwnDistanceMin}, mean {stats.OwnDistanceMean:F1}, " +
            $"median {stats.OwnDistanceMedian}, max {stats.OwnDistanceMax}  <- referenceFloor candidate");
        Console.WriteLine(
            $"Margin to best different artwork (correct matches only): min {stats.MarginMin}, mean {stats.MarginMean:F1}, " +
            $"median {stats.MarginMedian}, max {stats.MarginMax}");
    }

    private sealed record RoundTripGateArgs(
        string CacheDir, string? IndexPath, string? OutPath, int LandCount, int NonLandCount, int Seed, double MinRank1Rate, bool Force);

    private static RoundTripGateArgs ParseArgs(string[] args)
    {
        string? cacheDir = null;
        string? indexPath = null;
        string? outPath = null;
        var landCount = DefaultLandSampleSize;
        var nonLandCount = DefaultNonLandSampleSize;
        var seed = DefaultSeed;
        var minRank1Rate = DefaultMinRank1Rate;
        var force = false;

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
                case "--lands" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out landCount) || landCount < 0)
                    {
                        throw new ArgumentException("round-trip-gate: --lands must be a non-negative integer.");
                    }

                    break;
                case "--non-lands" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out nonLandCount) || nonLandCount < 0)
                    {
                        throw new ArgumentException("round-trip-gate: --non-lands must be a non-negative integer.");
                    }

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("round-trip-gate: --seed must be an integer.");
                    }

                    break;
                case "--min-rank1-rate" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out minRank1Rate) || minRank1Rate < 0 || minRank1Rate > 1)
                    {
                        throw new ArgumentException("round-trip-gate: --min-rank1-rate must be between 0 and 1.");
                    }

                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new ArgumentException($"round-trip-gate: unrecognised argument \"{args[i]}\".");
            }
        }

        if (cacheDir is null)
        {
            throw new ArgumentException("round-trip-gate: --cache <dir> is required.");
        }

        return new RoundTripGateArgs(cacheDir, indexPath, outPath, landCount, nonLandCount, seed, minRank1Rate, force);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              round-trip-gate --cache <dir> [--index <path>] [--out <thresholds.json>]
                               [--lands N] [--non-lands N] [--seed N]
                               [--min-rank1-rate F] [--force]

            Package B2's round-trip gate: samples --lands + --non-lands
            artworks (default 20 + 180) deterministically from the
            committed index (default data/index/cards.lfidx), presents
            each cached Scryfall render through the shipping query path
            (ICardIdentifier.Identify), and reports the rank-1 ArtworkId
            match rate and the distance/margin distributions.

            --out writes data/index/thresholds.json's referenceFloor and
            margin statistics (see RoundTripThresholdsDocument -- NOT
            goodDistance/okDistance, which are B6's job). Refuses to write
            if the rank-1 rate is below --min-rank1-rate (default 0.99)
            unless --force is also given.
            """);
    }
}
