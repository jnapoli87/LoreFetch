using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Scanning;
using LoreFetch.Lab.Accuracy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoreFetch.Lab;

/// `lab accuracy [--index <path>] [--ok-distance N] [--max-wrong N]`
///
/// Package B6's accuracy harness, run against whatever subset of the H3
/// fixture corpus (`test-images/ground-truth.csv` + `test-images/fixtures/`)
/// exists on disk -- see `AccuracyCorpusLoader`. Reports correct@1/wrong@1/
/// no-match per height and per rung, the margin distribution, and the
/// lands-excluded count, then evaluates the run-fails-if gate.
///
/// **Never writes `goodDistance`/`okDistance` into `thresholds.json`** --
/// see `ThresholdsCalibration`'s own doc comment for why: those must be
/// calibrated from the REAL corpus, and this command has no way to tell a
/// full corpus from a placeholder one other than the coverage line it
/// always prints. A future `--write-thresholds` flag is deliberately NOT
/// implemented here; whoever runs this against the completed H3 corpus
/// should call `ThresholdsCalibration.Suggest`/`Write` explicitly, by hand,
/// after reviewing the printed report -- not as a side effect of a
/// default-argument CLI invocation.
public static class AccuracyCommand
{
    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        AccuracyArgs parsed;
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

        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            Console.Error.WriteLine("accuracy: could not locate the repository automatically.");
            return 1;
        }

        var indexPath = parsed.IndexPath ?? Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"accuracy: index not found at \"{indexPath}\".");
            return 1;
        }

        var okDistance = ResolveOkDistance(repoRoot!, parsed.OkDistance);

        // `--images-root` overrides ONLY where ground-truth.csv/fixtures are
        // resolved from -- never the index default (still repoRoot-derived
        // above) or ResolveOkDistance's thresholds-file lookup (still
        // repoRoot-derived too). This exists because `test-images/` is
        // gitignored per-checkout (DECISIONS.md "Never commit card imagery"),
        // so a linked worktree's own `test-images/` is a separate, possibly
        // stale copy from the main checkout's -- and `RepoPaths.TryFindRepoRoot`
        // resolves to whichever checkout the running exe's own bin/ (or cwd)
        // sits under, which for an exe built in a worktree is always that
        // worktree, never the main checkout, regardless of the process's
        // working directory (AppContext.BaseDirectory is tried first). Pass
        // `--images-root <path-to-a-checkout>` to point this run's corpus
        // resolution at a DIFFERENT checkout's `test-images/` without
        // rebuilding or copying imagery anywhere (DECISIONS.md: imagery is
        // never copied into a worktree).
        var imagesRoot = parsed.ImagesRoot ?? repoRoot!;

        var groundTruthPath = Path.Combine(imagesRoot, AccuracyCorpusLoader.GroundTruthRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(groundTruthPath))
        {
            Console.WriteLine($"accuracy: no ground-truth corpus yet at \"{groundTruthPath}\" -- nothing to run. " +
                "This is expected until H3 delivers fixtures; the harness itself is exercised by the synthetic tests in Tests/Lab.");
            return 0;
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
            Console.Error.WriteLine($"accuracy: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        IReadOnlyList<GroundTruthRow> rawRows;
        try
        {
            rawRows = GroundTruthCsvReader.Read(groundTruthPath);
        }
        catch (GroundTruthCsvFormatException ex)
        {
            Console.Error.WriteLine($"accuracy: {ex.Message}");
            return 1;
        }

        IReadOnlyList<ResolvedGroundTruthRow> resolved;
        try
        {
            resolved = GroundTruthOracleLookup.Resolve(index, rawRows);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"accuracy: {ex.Message}");
            return 1;
        }

        var allFrames = GroundTruthFrame.GroupByFile(resolved);
        var (found, missing, coverage) = AccuracyCorpusLoader.SplitByPresence(imagesRoot, allFrames);

        Console.WriteLine($"Ground truth: {groundTruthPath} ({allFrames.Count} frame(s), {resolved.Count} slot(s))");
        Console.WriteLine(coverage.Summarize());
        if (missing.Count > 0)
        {
            Console.WriteLine($"({missing.Count} frame(s) named in ground-truth.csv have no fixture file on disk yet -- skipped, not counted as failures.)");
        }

        if (found.Count == 0)
        {
            Console.WriteLine("accuracy: no fixture files found on disk for any ground-truth frame -- nothing to run.");
            return 0;
        }

        var options = new AccuracyHarnessOptions
        {
            OkDistance = okDistance,
            MaxWrongAt1AtOkDistance = parsed.MaxWrong,
        };

        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var rectifier = new PerspectiveRectifier();

        var allResults = new List<SlotAccuracyResult>();
        foreach (var frame in found)
        {
            var path = AccuracyCorpusLoader.ResolveFixturePath(imagesRoot, frame.File);
            using var mat = AccuracyCorpusLoader.LoadFixtureMat(path);
            using var cameraFrame = FrameMat.FromMat(mat);
            allResults.AddRange(AccuracyFrameRunner.Run(frame, cameraFrame, detector, rectifier, identifier, options));
        }

        var stats = AccuracyStatistics.From(allResults);
        var gate = AccuracyGateResult.Evaluate(stats, options);

        Console.WriteLine();
        Console.WriteLine(AccuracyReportFormatter.Format(coverage, stats, gate, options));

        return gate.Passed ? 0 : 1;
    }

    private sealed record AccuracyArgs(string? IndexPath, int? OkDistance, int MaxWrong, string? ImagesRoot);

    /// `--ok-distance` wins when given. Otherwise, the committed
    /// `data/index/thresholds.json`'s own `okDistance` (B6's calibrated
    /// value, real-corpus-derived) -- DECISIONS.md/CONTRACTS.md: "nothing may
    /// hardcode a distance; the thresholds file is the one source of
    /// truth." Falls back to `AccuracyHarnessOptions.Default.OkDistance`
    /// (the documented CardSpotter-prior placeholder, 270) only when the
    /// thresholds file cannot be loaded at all -- e.g. a checkout that
    /// predates B6's calibration, or a corrupted file -- and says so on
    /// stderr rather than silently substituting a different number than
    /// the one the committed file names.
    internal static int ResolveOkDistance(string repoRoot, int? overrideValue)
    {
        if (overrideValue.HasValue)
        {
            return overrideValue.Value;
        }

        var thresholdsPath = Path.Combine(repoRoot, "data", "index", "thresholds.json");
        try
        {
            return ThresholdsFile.Load(thresholdsPath).OkDistance;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(
                $"accuracy: could not load OkDistance from \"{thresholdsPath}\" ({ex.Message}); " +
                $"falling back to the documented default {AccuracyHarnessOptions.Default.OkDistance}.");
            return AccuracyHarnessOptions.Default.OkDistance;
        }
    }

    private static AccuracyArgs ParseArgs(string[] args)
    {
        string? indexPath = null;
        int? okDistance = null;
        var maxWrong = AccuracyHarnessOptions.Default.MaxWrongAt1AtOkDistance;
        string? imagesRoot = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length:
                    indexPath = args[++i];
                    break;
                case "--ok-distance" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedOkDistance))
                    {
                        throw new ArgumentException("accuracy: --ok-distance must be an integer.");
                    }

                    okDistance = parsedOkDistance;
                    break;
                case "--max-wrong" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxWrong) || maxWrong < 0)
                    {
                        throw new ArgumentException("accuracy: --max-wrong must be a non-negative integer.");
                    }

                    break;
                case "--images-root" when i + 1 < args.Length:
                    imagesRoot = args[++i];
                    break;
                default:
                    throw new ArgumentException($"accuracy: unrecognised argument \"{args[i]}\".");
            }
        }

        return new AccuracyArgs(indexPath, okDistance, maxWrong, imagesRoot);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              accuracy [--index <path>] [--ok-distance N] [--max-wrong N] [--images-root <checkout>]

            --images-root overrides where test-images/ground-truth.csv and
            test-images/fixtures/ are resolved from (default: the same
            checkout the index defaults from). Use it to point a worktree
            build at a different checkout's test-images/ -- e.g. the main
            checkout's -- without copying imagery anywhere.

            Package B6: runs the accuracy harness against whatever subset of
            test-images/ground-truth.csv + test-images/fixtures/ exists on
            disk (H3's corpus, delivered in batches). Reports correct@1/
            wrong@1/no-match per height and per rung, the margin
            distribution, and the lands-excluded count. Never writes
            thresholds.json -- see ThresholdsCalibration.

            --ok-distance defaults to the committed
            data/index/thresholds.json's own okDistance (falling back to
            the documented CardSpotter-prior placeholder if that file
            cannot be loaded); pass --ok-distance to override it for a
            one-off run.
            """);
    }
}
