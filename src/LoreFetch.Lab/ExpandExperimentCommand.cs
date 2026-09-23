using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.Accuracy;
using LoreFetch.Lab.CropScale;
using LoreFetch.Lab.Images;
using LoreFetch.Lab.RoundTrip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace LoreFetch.Lab;

/// `lab expand-experiment [--images-root <dir>] [--index <path>] [--cache <dir>]
///    [--file-prefix <prefix>] [--expand-w F] [--expand-h F] [--ok-distance N]
///    [--border-sample N] [--seed N]`
///
/// Package E1a's crop-expansion experiment. The orchestrator's finding this
/// answers: on `tight_white` and the matching black-mat frame
/// (`normal_black_matches_tight_white`), `lab detect` finds good-looking
/// quads, but the rectified crop sits on the INNER edge of the card's black
/// border (the border is cropped away; the coloured inner frame fills the
/// crop) -- exactly the crop-scale failure B5c's curve quantified, and
/// every card on those two frames identifies at a noise-level distance
/// (~290-350). This command asks, per card, across all frames the caller
/// points it at: does growing the DETECTED QUAD (before rectification, via
/// `QuadExpansion`) by a measured correction factor fix it -- and does
/// doing so ever cost a card that was already correctly identified?
///
/// Three hypotheses are computed per slot: `factor 1.0` (today's shipped
/// geometry, unchanged), `expanded` (the quad grown by the measured
/// border-correction factor before rectifying), and `dual` (identify BOTH,
/// keep whichever oracle's distance is lower per oracle -- the same
/// "best-of-both" reduction `HashCardIdentifier` already uses internally
/// for its two query orientations, generalized here over two QUAD
/// hypotheses instead of two orientations of one quad). This never touches
/// `HashCardIdentifier`, `ContourCardDetector` or `PerspectiveRectifier` --
/// it calls them exactly as `identify`/`accuracy` do, from the outside.
///
/// Diagnostic-only, `LoreFetch.Lab`-scoped -- same status as `crop-scale`
/// and `retrieval-experiment`. Never writes to any committed file; output
/// is transcribed into `docs/accuracy.md` (or the E1a package report) by
/// hand.
public static class ExpandExperimentCommand
{
    public const string DefaultFilePrefix = "integration_corpus/";
    public const int DefaultBorderSampleSize = 20;
    public const int DefaultSeed = 20260922;
    public const int DefaultTopN = 5;

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        ExpandArgs parsed;
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
            Console.Error.WriteLine("expand-experiment: could not locate the repository automatically.");
            return 1;
        }

        var indexPath = parsed.IndexPath ?? Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"expand-experiment: index not found at \"{indexPath}\".");
            return 1;
        }

        var imagesRoot = parsed.ImagesRoot ?? repoRoot!;
        var okDistance = AccuracyCommand.ResolveOkDistance(repoRoot!, parsed.OkDistance);

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
            Console.Error.WriteLine($"expand-experiment: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        // --- Resolve the expansion factors -----------------------------
        float widthFactor;
        float heightFactor;
        if (parsed.ExpandWidth.HasValue && parsed.ExpandHeight.HasValue)
        {
            widthFactor = parsed.ExpandWidth.Value;
            heightFactor = parsed.ExpandHeight.Value;
            Console.WriteLine($"Expansion factors given explicitly: width x{widthFactor:0.###}, height x{heightFactor:0.###}.");
        }
        else if (parsed.CacheDir is not null)
        {
            if (!Directory.Exists(parsed.CacheDir))
            {
                Console.Error.WriteLine($"expand-experiment: --cache directory not found: \"{parsed.CacheDir}\".");
                return 1;
            }

            var measured = MeasureBorderFromCache(index, parsed.CacheDir, parsed.BorderSampleSize, parsed.Seed);
            if (measured is null)
            {
                Console.Error.WriteLine(
                    "expand-experiment: could not measure a border ratio from --cache (no sampled renders were " +
                    "found on disk) -- pass --expand-w/--expand-h explicitly instead.");
                return 1;
            }

            widthFactor = parsed.ExpandWidth ?? measured.Value.WidthCorrectionFactorMean;
            heightFactor = parsed.ExpandHeight ?? measured.Value.HeightCorrectionFactorMean;

            Console.WriteLine(
                $"Measured black-border ratio over {measured.Value.SampleCount} real Scryfall renders " +
                $"(cache: {parsed.CacheDir}):");
            Console.WriteLine(
                $"  width  border fraction: min {measured.Value.WidthFractionMin:0.###} / mean {measured.Value.WidthFractionMean:0.###} / max {measured.Value.WidthFractionMax:0.###}" +
                $" -> correction factor mean x{measured.Value.WidthCorrectionFactorMean:0.###}");
            Console.WriteLine(
                $"  height border fraction: min {measured.Value.HeightFractionMin:0.###} / mean {measured.Value.HeightFractionMean:0.###} / max {measured.Value.HeightFractionMax:0.###}" +
                $" -> correction factor mean x{measured.Value.HeightCorrectionFactorMean:0.###}");
            Console.WriteLine($"Using measured expansion: width x{widthFactor:0.###}, height x{heightFactor:0.###}.");
        }
        else
        {
            Console.Error.WriteLine(
                "expand-experiment: pass either --expand-w/--expand-h explicitly, or --cache <dir> to measure them " +
                "from real Scryfall renders.");
            return 1;
        }

        // --- Load ground truth, filtered to the frames this run covers -
        var groundTruthPath = Path.Combine(imagesRoot, AccuracyCorpusLoader.GroundTruthRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(groundTruthPath))
        {
            Console.Error.WriteLine($"expand-experiment: ground truth not found at \"{groundTruthPath}\".");
            return 1;
        }

        IReadOnlyList<GroundTruthRow> rawRows;
        IReadOnlyList<ResolvedGroundTruthRow> resolved;
        try
        {
            rawRows = GroundTruthCsvReader.Read(groundTruthPath);
            resolved = GroundTruthOracleLookup.Resolve(index, rawRows);
        }
        catch (Exception ex) when (ex is GroundTruthCsvFormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"expand-experiment: {ex.Message}");
            return 1;
        }

        var allFrames = GroundTruthFrame.GroupByFile(resolved);
        var frames = allFrames.Where(f => f.File.StartsWith(parsed.FilePrefix, StringComparison.Ordinal)).ToList();
        if (frames.Count == 0)
        {
            Console.WriteLine($"expand-experiment: no ground-truth frame's file starts with \"{parsed.FilePrefix}\" -- nothing to run.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"Running {frames.Count} frame(s) matching prefix \"{parsed.FilePrefix}\" from {groundTruthPath}, OkDistance={okDistance}.");
        Console.WriteLine();

        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var rectifier = new PerspectiveRectifier();

        var allRows = new List<SlotExpandResult>();
        foreach (var frame in frames)
        {
            var path = AccuracyCorpusLoader.ResolveFixturePath(imagesRoot, frame.File);
            using var mat = AccuracyCorpusLoader.LoadFixtureMat(path);
            using var cameraFrame = FrameMat.FromMat(mat);

            var detected = detector.Detect(cameraFrame, frame.Layout);
            var (rows, cols) = SlotMapper.GridDimensionsForLayout(frame.Layout);

            if (!SlotMapper.TryInferGrid(detected, rows, cols, out var cellsBySlot))
            {
                Console.WriteLine($"{frame.File}: grid could not be inferred ({detected.Count} detected, expected {frame.Layout}) -- every slot DROPPED.");
                foreach (var slot in frame.Slots)
                {
                    allRows.Add(SlotExpandResult.Dropped(frame.File, slot));
                }

                continue;
            }

            for (var i = 0; i < frame.Slots.Count; i++)
            {
                var slot = frame.Slots[i];
                var quad = cellsBySlot![i];
                if (quad is null)
                {
                    allRows.Add(SlotExpandResult.NoDetection(frame.File, slot));
                    continue;
                }

                var baselineCard = rectifier.Rectify(cameraFrame, quad.Value);
                var baselineCandidates = identifier.Identify(baselineCard, parsed.TopN);

                var expandedQuad = QuadExpansion.Expand(quad.Value, widthFactor, heightFactor);
                var expandedCard = rectifier.Rectify(cameraFrame, expandedQuad);
                var expandedCandidates = identifier.Identify(expandedCard, parsed.TopN);

                var dualCandidates = MergeByMinDistance(baselineCandidates, expandedCandidates);

                allRows.Add(SlotExpandResult.Identified(
                    frame.File, slot,
                    Top1(baselineCandidates), Top1(expandedCandidates), Top1(dualCandidates)));
            }
        }

        PrintPerCardTable(allRows, okDistance);
        PrintSummary(allRows, okDistance);

        return 0;
    }

    // --- Hypothesis merge -----------------------------------------------

    /// `HashCardIdentifier`'s own internal reduction (best distance per
    /// OracleId across its two query orientations) generalized here over
    /// two QUAD hypotheses (baseline vs. expanded) instead of two
    /// orientations of one quad -- see this type's own doc comment. Not a
    /// change to `ICardIdentifier` or any shipped type; this is a plain
    /// list merge over two already-computed candidate lists.
    private static IReadOnlyList<CardCandidate> MergeByMinDistance(
        IReadOnlyList<CardCandidate> a, IReadOnlyList<CardCandidate> b)
    {
        var merged = new Dictionary<string, CardCandidate>(StringComparer.Ordinal);
        void Merge(IReadOnlyList<CardCandidate> list)
        {
            foreach (var c in list)
            {
                if (!merged.TryGetValue(c.OracleId, out var existing) || c.Distance < existing.Distance)
                {
                    merged[c.OracleId] = c;
                }
            }
        }

        Merge(a);
        Merge(b);
        return merged.Values.OrderBy(c => c.Distance).ToList();
    }

    private static CardCandidate? Top1(IReadOnlyList<CardCandidate> candidates) =>
        candidates.Count > 0 ? candidates[0] : null;

    // --- Border measurement ----------------------------------------------

    private readonly record struct BorderSummary(
        int SampleCount,
        float WidthFractionMin, float WidthFractionMean, float WidthFractionMax,
        float HeightFractionMin, float HeightFractionMean, float HeightFractionMax,
        float WidthCorrectionFactorMean, float HeightCorrectionFactorMean);

    /// Samples `sampleSize` NON-LAND entries deterministically from the
    /// committed index (same sampler B2/B5c use -- `RoundTripSampler`, so
    /// "the same seed measures the same sample" holds here too), decodes
    /// each cached render, and measures `BorderRatioMeasurement` over it.
    /// Lands are excluded because many are full-art with no conventional
    /// black frame (CLAUDE.md's Ladder note: "full-art lands also lack a
    /// type line where the hash region expects one") -- a land's own
    /// border geometry would not represent the black-bordered NORMAL cards
    /// this experiment's frames are actually testing (CLAUDE.md's "Measure
    /// accuracy on normal cards only" applies here too, one level down, to
    /// what BUILDS the correction factor).
    private static BorderSummary? MeasureBorderFromCache(HashIndexData index, string cacheDir, int sampleSize, int seed)
    {
        var nonLandEntries = index.Entries.Where(e => !e.IsBasicLand).ToList();
        var sample = RoundTripSampler.SelectUniformSample(nonLandEntries, sampleSize, seed);

        var widthFractions = new List<float>();
        var heightFractions = new List<float>();

        foreach (var entry in sample)
        {
            var imagePath = ImageCache.GetImagePath(cacheDir, entry.ArtworkId);
            if (!File.Exists(imagePath))
            {
                continue;
            }

            using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (mat.Empty())
            {
                continue;
            }

            var measurement = BorderRatioMeasurement.Measure(mat);
            widthFractions.Add(measurement.WidthBorderFraction);
            heightFractions.Add(measurement.HeightBorderFraction);
        }

        if (widthFractions.Count == 0)
        {
            return null;
        }

        var widthMean = widthFractions.Average();
        var heightMean = heightFractions.Average();

        return new BorderSummary(
            SampleCount: widthFractions.Count,
            WidthFractionMin: widthFractions.Min(), WidthFractionMean: widthMean, WidthFractionMax: widthFractions.Max(),
            HeightFractionMin: heightFractions.Min(), HeightFractionMean: heightMean, HeightFractionMax: heightFractions.Max(),
            WidthCorrectionFactorMean: 1f / (1f - widthMean),
            HeightCorrectionFactorMean: 1f / (1f - heightMean));
    }

    // --- Reporting ---------------------------------------------------------

    private sealed record SlotExpandResult(
        string File, ResolvedGroundTruthRow Slot, string Status,
        CardCandidate? Baseline, CardCandidate? Expanded, CardCandidate? Dual)
    {
        public static SlotExpandResult Dropped(string file, ResolvedGroundTruthRow slot) =>
            new(file, slot, "DroppedFrame", null, null, null);

        public static SlotExpandResult NoDetection(string file, ResolvedGroundTruthRow slot) =>
            new(file, slot, "NoDetection", null, null, null);

        public static SlotExpandResult Identified(
            string file, ResolvedGroundTruthRow slot, CardCandidate? baseline, CardCandidate? expanded, CardCandidate? dual) =>
            new(file, slot, "Identified", baseline, expanded, dual);
    }

    private static void PrintPerCardTable(IReadOnlyList<SlotExpandResult> rows, int okDistance)
    {
        Console.WriteLine("file,slot,oracle_name,rung,baseline_dist,baseline_ok,expanded_dist,expanded_ok,dual_dist,dual_ok");
        foreach (var r in rows)
        {
            var truth = r.Slot.ExpectedOracleId;
            string Col(CardCandidate? c) => c is null
                ? "-,-"
                : $"{c.Value.Distance},{(string.Equals(c.Value.OracleId, truth, StringComparison.Ordinal) ? "Y" : "N")}";

            Console.WriteLine(
                $"{r.File},{r.Slot.Row.Slot},{r.Slot.Row.OracleName},{r.Slot.Row.Rung}," +
                $"{Col(r.Baseline)},{Col(r.Expanded)},{Col(r.Dual)}");
        }

        Console.WriteLine();
    }

    private static void PrintSummary(IReadOnlyList<SlotExpandResult> rows, int okDistance)
    {
        bool IsCorrect(CardCandidate? c, string expectedOracleId) =>
            c is not null && string.Equals(c.Value.OracleId, expectedOracleId, StringComparison.Ordinal);

        var identified = rows.Where(r => r.Status == "Identified").ToList();
        var normalOnly = identified.Where(r => !r.Slot.IsBasicLand).ToList();

        Console.WriteLine("=== Summary (CLAUDE.md Ladder: lands excluded from every count below) ===");
        Console.WriteLine($"Identified slots: {identified.Count} total, {normalOnly.Count} non-land, " +
            $"{rows.Count(r => r.Status == "NoDetection")} no-detection, {rows.Count(r => r.Status == "DroppedFrame")} dropped-frame.");
        Console.WriteLine();

        int CorrectCount(IEnumerable<SlotExpandResult> set, Func<SlotExpandResult, CardCandidate?> pick) =>
            set.Count(r => IsCorrect(pick(r), r.Slot.ExpectedOracleId));

        Console.WriteLine("Overall correct@1 (non-land):");
        Console.WriteLine($"  baseline (factor 1.0): {CorrectCount(normalOnly, r => r.Baseline)} / {normalOnly.Count}");
        Console.WriteLine($"  expanded:               {CorrectCount(normalOnly, r => r.Expanded)} / {normalOnly.Count}");
        Console.WriteLine($"  dual (min of both):     {CorrectCount(normalOnly, r => r.Dual)} / {normalOnly.Count}");
        Console.WriteLine();

        // (a) does expansion rescue tight_white and the black-mat frame?
        Console.WriteLine("(a) Per-frame correct@1 (non-land) -- rescue check:");
        foreach (var group in normalOnly.GroupBy(r => r.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var g = group.ToList();
            Console.WriteLine(
                $"  {group.Key}: baseline {CorrectCount(g, r => r.Baseline)}/{g.Count}, " +
                $"expanded {CorrectCount(g, r => r.Expanded)}/{g.Count}, dual {CorrectCount(g, r => r.Dual)}/{g.Count}");
        }

        Console.WriteLine();

        // (b) does dual-hypothesis ever make a correctly-identified card wrong?
        var regressions = normalOnly.Where(r => IsCorrect(r.Baseline, r.Slot.ExpectedOracleId) && !IsCorrect(r.Dual, r.Slot.ExpectedOracleId)).ToList();
        Console.WriteLine($"(b) Baseline-correct, dual-wrong (regressions): {regressions.Count}");
        foreach (var r in regressions)
        {
            Console.WriteLine($"    {r.File} slot {r.Slot.Row.Slot} ({r.Slot.Row.OracleName}): baseline dist {r.Baseline!.Value.Distance} -> dual top1 {r.Dual!.Value.OracleName} dist {r.Dual.Value.Distance}");
        }

        Console.WriteLine();

        // (c) does it create any wrong match under OkDistance?
        var expandedWrongInside = identified.Where(r => !IsCorrect(r.Expanded, r.Slot.ExpectedOracleId) && r.Expanded is not null && r.Expanded.Value.Distance <= okDistance).ToList();
        var dualWrongInside = identified.Where(r => !IsCorrect(r.Dual, r.Slot.ExpectedOracleId) && r.Dual is not null && r.Dual.Value.Distance <= okDistance).ToList();
        Console.WriteLine($"(c) Wrong match at distance <= OkDistance ({okDistance}):");
        Console.WriteLine($"    expanded hypothesis: {expandedWrongInside.Count}");
        foreach (var r in expandedWrongInside)
        {
            Console.WriteLine($"      {r.File} slot {r.Slot.Row.Slot} ({r.Slot.Row.OracleName}, rung={r.Slot.Row.Rung}): expanded top1 {r.Expanded!.Value.OracleName} dist {r.Expanded.Value.Distance}");
        }

        Console.WriteLine($"    dual hypothesis:     {dualWrongInside.Count}");
        foreach (var r in dualWrongInside)
        {
            Console.WriteLine($"      {r.File} slot {r.Slot.Row.Slot} ({r.Slot.Row.OracleName}, rung={r.Slot.Row.Rung}): dual top1 {r.Dual!.Value.OracleName} dist {r.Dual.Value.Distance}");
        }
    }

    // --- CLI ---------------------------------------------------------------

    private sealed record ExpandArgs(
        string? ImagesRoot, string? IndexPath, string? CacheDir, string FilePrefix,
        float? ExpandWidth, float? ExpandHeight, int? OkDistance, int BorderSampleSize, int Seed, int TopN);

    private static ExpandArgs ParseArgs(string[] args)
    {
        string? imagesRoot = null;
        string? indexPath = null;
        string? cacheDir = null;
        var filePrefix = DefaultFilePrefix;
        float? expandWidth = null;
        float? expandHeight = null;
        int? okDistance = null;
        var borderSampleSize = DefaultBorderSampleSize;
        var seed = DefaultSeed;
        var topN = DefaultTopN;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--images-root" when i + 1 < args.Length:
                    imagesRoot = args[++i];
                    break;
                case "--index" when i + 1 < args.Length:
                    indexPath = args[++i];
                    break;
                case "--cache" when i + 1 < args.Length:
                    cacheDir = args[++i];
                    break;
                case "--file-prefix" when i + 1 < args.Length:
                    filePrefix = args[++i];
                    break;
                case "--expand-w" when i + 1 < args.Length:
                    if (!float.TryParse(args[++i], out var w) || w <= 0f)
                    {
                        throw new ArgumentException("expand-experiment: --expand-w must be a positive number.");
                    }

                    expandWidth = w;
                    break;
                case "--expand-h" when i + 1 < args.Length:
                    if (!float.TryParse(args[++i], out var h) || h <= 0f)
                    {
                        throw new ArgumentException("expand-experiment: --expand-h must be a positive number.");
                    }

                    expandHeight = h;
                    break;
                case "--ok-distance" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedOk))
                    {
                        throw new ArgumentException("expand-experiment: --ok-distance must be an integer.");
                    }

                    okDistance = parsedOk;
                    break;
                case "--border-sample" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out borderSampleSize) || borderSampleSize < 1)
                    {
                        throw new ArgumentException("expand-experiment: --border-sample must be a positive integer.");
                    }

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("expand-experiment: --seed must be an integer.");
                    }

                    break;
                case "--top" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out topN) || topN < 1)
                    {
                        throw new ArgumentException("expand-experiment: --top must be a positive integer.");
                    }

                    break;
                default:
                    throw new ArgumentException($"expand-experiment: unrecognised argument \"{args[i]}\".");
            }
        }

        if (expandWidth.HasValue != expandHeight.HasValue)
        {
            throw new ArgumentException("expand-experiment: pass both --expand-w and --expand-h, or neither (to auto-measure from --cache).");
        }

        return new ExpandArgs(imagesRoot, indexPath, cacheDir, filePrefix, expandWidth, expandHeight, okDistance, borderSampleSize, seed, topN);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              expand-experiment [--images-root <dir>] [--index <path>] [--cache <dir>]
                                 [--file-prefix <prefix>] [--expand-w F] [--expand-h F]
                                 [--ok-distance N] [--border-sample N] [--seed N] [--top N]

            Package E1a: for every ground-truth slot whose File starts with
            --file-prefix (default "integration_corpus/"), runs detect ->
            (rectify at factor 1.0) -> Identify, detect -> (QuadExpansion.Expand
            -> rectify) -> Identify, and a "dual hypothesis" (best distance per
            oracle across both) -- reporting per-card distance/correctness for
            all three, then a summary of whether expansion rescues detection-
            inset frames, whether dual-hypothesis ever costs an already-correct
            card, and whether it creates any wrong match inside OkDistance.

            --expand-w/--expand-h give the per-axis expansion factor directly
            (1.0 = no-op, >1.0 grows the quad). Omit both to auto-measure them
            from real Scryfall renders in --cache (BorderRatioMeasurement over
            --border-sample non-land entries, default 20, seed 20260922).
            """);
    }
}
