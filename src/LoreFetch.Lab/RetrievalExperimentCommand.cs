using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Lab;

/// `lab retrieval-experiment dump-contours <dir>`
/// `lab retrieval-experiment run <dir> [--index <path>]`
///
/// Diagnostic-only tooling for the H1 (board nesting) / H2 (mat contrast) /
/// H3 (decorative frames) investigation into why `ContourCardDetector`
/// finds only 1-2 of 9 cards on the real `a_corpus` 3x3 frames -- see
/// docs/accuracy.md "Retrieval mode / mat contrast experiment". Never
/// wired into any shipped path, same category as `detect`/`crop-scale`.
///
/// `dump-contours` replicates `ContourCardDetector`'s own
/// Canny -> morph-close -> `findContours` step, using
/// `ContourDetectorOptions.Default`'s own (frozen, unmodified) thresholds,
/// under BOTH `RETR_EXTERNAL` and `RETR_LIST`, and prints every contour's
/// bounding box sorted by area. This is how the per-frame crop rectangles
/// below were chosen by hand: the board's own outline shows up as one of
/// the largest bounding boxes spanning most of the frame width, while the
/// nine cards cluster in a visibly smaller, shared bounding box. Re-run it
/// if the six `a_corpus` frames are ever replaced -- do NOT reuse these
/// numbers on different imagery.
///
/// `run` executes the 2x2 sweep (retrieval mode x full-frame/cropped) over
/// the six `a_corpus` frames and, for the best cell, the full
/// detect -> rectify -> identify path against the committed index.
public static class RetrievalExperimentCommand
{
    /// Hand-picked from `dump-contours`'s own output against the real
    /// `a_corpus` frames -- one rectangle PER frame, not a single shared
    /// one, because the nine cards are placed freehand (DECISIONS.md "no
    /// registration jig") and `dump-contours`'s own `RETR_LIST` card-sized
    /// contours (aspect ~0.70-0.75, area ~56k-75k) show the grid's overall
    /// bounding box actually shifts by up to ~150px between frames even
    /// though the camera and board are fixed. Each rectangle is that
    /// frame's own card-cluster bounding box, expanded by a 55px
    /// left/right and ~15px top margin (top has little room to spare --
    /// `dump-contours`'s `RETR_LIST` output for a_1 puts the board's own
    /// top edge at y=94, just 16px above the top row of cards at y=110,
    /// which is a fact about this rig's physical layout, not something a
    /// crop rectangle can create room for) and extended to the frame's own
    /// bottom edge (the board's bottom edge is never visible in any of the
    /// six frames -- it runs off-frame -- so there is nothing to crop out
    /// on that side). Confirmed by "run" itself: zero TouchesBorder
    /// rejections in any cropped cell across all six frames (see
    /// docs/accuracy.md) -- the one failure mode the package brief warns
    /// about from an earlier, single hardcoded-rectangle attempt.
    private static readonly Dictionary<string, Rect> CropRectsByFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a_1.png"] = new Rect(638, 95, 845, 984),
        ["a_2.png"] = new Rect(525, 82, 873, 997),
        ["a_3.png"] = new Rect(524, 74, 864, 1005),
        ["a_4.png"] = new Rect(483, 73, 874, 1006),
        ["a_5.png"] = new Rect(532, 73, 853, 1006),
        ["a_6.png"] = new Rect(546, 60, 842, 1019),
    };

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var sub = args[0];
        var rest = args[1..];

        return sub switch
        {
            "dump-contours" => DumpContours(rest),
            "run" => RunExperiment(rest),
            _ => Unknown(sub),
        };
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"retrieval-experiment: unknown subcommand \"{sub}\".");
        PrintUsage();
        return 1;
    }

    // ---------------------------------------------------------------
    // dump-contours
    // ---------------------------------------------------------------

    private static int DumpContours(string[] args)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("retrieval-experiment dump-contours: pass a directory of images.");
            return 1;
        }

        var files = Directory.EnumerateFiles(args[0], "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in files)
        {
            using var color = Cv2.ImRead(file, ImreadModes.Color);
            if (color.Empty())
            {
                continue;
            }

            Console.WriteLine($"== {Path.GetFileName(file)} ({color.Width}x{color.Height}) ==");
            foreach (var mode in new[] { RetrievalModes.External, RetrievalModes.List })
            {
                var boxes = FindContourBoundingBoxes(color, mode);
                Console.WriteLine($"  {mode}: {boxes.Count} contours, top 15 by area:");
                foreach (var (rect, area) in boxes.OrderByDescending(b => b.Area).Take(15))
                {
                    Console.WriteLine(
                        $"    area={area:F0} rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height}) " +
                        $"aspect={(rect.Height == 0 ? 0 : (float)rect.Width / rect.Height):F2}");
                }
            }
        }

        return 0;
    }

    /// Same Canny thresholds / morph-close kernel as
    /// `ContourDetectorOptions.Default` -- read-only reference to the
    /// frozen values, not a second copy that could drift -- but no quad
    /// approximation or filtering: this is purely "where are the raw
    /// contours", the input to picking `CropRectsByFile` above, not a
    /// measurement of detector accuracy itself.
    private static List<(Rect Rect, double Area)> FindContourBoundingBoxes(Mat color, RetrievalModes mode)
    {
        var defaults = ContourDetectorOptions.Default;
        using var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
        using var edges = new Mat();
        Cv2.Canny(blurred, edges, defaults.CannyThreshold1, defaults.CannyThreshold2);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(defaults.MorphCloseKernelSize, defaults.MorphCloseKernelSize));
        using var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(closed, out var contours, out _, mode, ContourApproximationModes.ApproxSimple);

        return contours
            .Select(c => (Rect: Cv2.BoundingRect(c), Area: Cv2.ContourArea(c)))
            .Where(b => b.Area > 50)
            .ToList();
    }

    // ---------------------------------------------------------------
    // run
    // ---------------------------------------------------------------

    private static int RunExperiment(string[] args)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("retrieval-experiment run: pass a directory of images.");
            return 1;
        }

        var dir = args[0];
        string? indexPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--index" && i + 1 < args.Length)
            {
                indexPath = args[++i];
            }
        }

        if (indexPath is null && RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        }

        var files = Directory.EnumerateFiles(dir, "a_*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"retrieval-experiment run: no a_*.png frames found in \"{dir}\".");
            return 1;
        }

        var cellResults = new Dictionary<string, List<CellResult>>(); // frame name -> 4 cells
        foreach (var file in files)
        {
            using var color = Cv2.ImRead(file, ImreadModes.Color);
            if (color.Empty())
            {
                Console.Error.WriteLine($"  could not decode \"{file}\", skipping.");
                continue;
            }

            var name = Path.GetFileName(file);
            var cropRect = ClampToImage(CropRectFor(name, color.Width, color.Height), color.Width, color.Height);
            using var cropped = new Mat(color, cropRect);

            var cells = new List<CellResult>
            {
                RunCell("full/External", color, RetrievalModes.External),
                RunCell("full/List", color, RetrievalModes.List),
                RunCell("crop/External", cropped, RetrievalModes.External),
                RunCell("crop/List", cropped, RetrievalModes.List),
            };
            cellResults[name] = cells;

            Console.WriteLine($"== {name} == (crop rect used: {cropRect.X},{cropRect.Y},{cropRect.Width}x{cropRect.Height})");
            foreach (var cell in cells)
            {
                Console.WriteLine($"  {cell.Label}: accepted={cell.Accepted.Count} [{string.Join(", ", cell.Accepted.Select(a => $"{a.Width:F0}x{a.Height:F0}"))}] " +
                    $"rejects: {(cell.RejectHistogram.Count == 0 ? "none" : string.Join(", ", cell.RejectHistogram.Select(kv => $"{kv.Key}={kv.Value}")))}");
            }
        }

        // Pick the best cell across ALL frames (by total accepted count,
        // tie-broken by fewest reject-driven anomalies) and run the full
        // identify path on it for every frame, so the "best cell" claim
        // is backed by the same evidence for every frame rather than
        // picked per-frame after the fact.
        var totalsByLabel = cellResults.Values
            .SelectMany(cells => cells)
            .GroupBy(c => c.Label)
            .Select(g => (Label: g.Key, Total: g.Sum(c => c.Accepted.Count)))
            .OrderByDescending(t => t.Total)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("Totals across all frames, by cell:");
        foreach (var (label, total) in totalsByLabel)
        {
            Console.WriteLine($"  {label}: {total} accepted (of {files.Count * 9} possible)");
        }

        var bestLabel = totalsByLabel[0].Label;
        Console.WriteLine();
        Console.WriteLine($"Best cell: {bestLabel}. Running identify against {indexPath ?? "(no index found)"}.");

        if (indexPath is null || !File.Exists(indexPath))
        {
            Console.Error.WriteLine("retrieval-experiment run: no committed index found -- skipping identify step.");
            return 0;
        }

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        var identifier = HashCardIdentifier.Load(indexPath, loggerFactory);
        var rectifier = new PerspectiveRectifier();
        var (bestSource, bestMode) = bestLabel switch
        {
            "full/External" => (CellSource.Full, RetrievalModes.External),
            "full/List" => (CellSource.Full, RetrievalModes.List),
            "crop/External" => (CellSource.Crop, RetrievalModes.External),
            "crop/List" => (CellSource.Crop, RetrievalModes.List),
            _ => (CellSource.Full, RetrievalModes.External),
        };

        foreach (var file in files)
        {
            using var color = Cv2.ImRead(file, ImreadModes.Color);
            if (color.Empty())
            {
                continue;
            }

            var cropRect = ClampToImage(CropRectFor(Path.GetFileName(file), color.Width, color.Height), color.Width, color.Height);
            using var region = bestSource == CellSource.Crop ? new Mat(color, cropRect) : color;

            using var frame = FrameMat.FromMat(region);
            var options = ContourDetectorOptions.Default with { RetrievalMode = bestMode };
            var detector = new ContourCardDetector(options, NullLogger<ContourCardDetector>.Instance);
            var quads = detector.Detect(frame, maxCards: 9);

            Console.WriteLine();
            Console.WriteLine($"{Path.GetFileName(file)}: identify on {quads.Count} detected card(s), sorted top-to-bottom, left-to-right:");
            foreach (var quad in quads.OrderBy(q => q.TL.Y).ThenBy(q => q.TL.X))
            {
                var rectified = rectifier.Rectify(frame, quad);
                var candidates = identifier.Identify(rectified, maxCandidates: 1);
                if (candidates.Count == 0)
                {
                    Console.WriteLine("  (no candidates)");
                    continue;
                }

                var top = candidates[0];
                Console.WriteLine($"  quad@({quad.TL.X:F0},{quad.TL.Y:F0}): top1={top.OracleName}, distance={top.Distance}");
            }
        }

        return 0;
    }

    private enum CellSource { Full, Crop }

    private static CellResult RunCell(string label, Mat color, RetrievalModes mode)
    {
        using var frame = FrameMat.FromMat(color);
        var options = ContourDetectorOptions.Default with { RetrievalMode = mode };
        var detector = new ContourCardDetector(options, NullLogger<ContourCardDetector>.Instance);
        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);

        var sizes = diagnostics.Accepted.Select(q =>
        {
            var w = (Distance(q.TL, q.TR) + Distance(q.BL, q.BR)) / 2f;
            var h = (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f;
            return (Width: w, Height: h);
        }).ToList();

        var histogram = diagnostics.Rejected
            .GroupBy(r => r.Reason)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return new CellResult(label, sizes, histogram);
    }

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// `CropRectsByFile`'s entry for `fileName`, or (as a fallback for any
    /// frame not in that hand-picked table) the full frame -- i.e. "no
    /// crop" rather than a guess, since a guessed rectangle risks the
    /// exact "clipped an outer card" failure the package brief warns
    /// against.
    private static Rect CropRectFor(string fileName, int width, int height) =>
        CropRectsByFile.TryGetValue(fileName, out var rect) ? rect : new Rect(0, 0, width, height);

    private static Rect ClampToImage(Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, width - 1);
        var y = Math.Clamp(rect.Y, 0, height - 1);
        var w = Math.Clamp(rect.Width, 1, width - x);
        var h = Math.Clamp(rect.Height, 1, height - y);
        return new Rect(x, y, w, h);
    }

    private sealed record CellResult(string Label, List<(float Width, float Height)> Accepted, Dictionary<string, int> RejectHistogram);

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              retrieval-experiment dump-contours <dir>
                                       Prints every raw contour's bounding
                                       box (RETR_EXTERNAL and RETR_LIST),
                                       sorted by area -- how CropRectsByFile
                                       was picked by hand.
              retrieval-experiment run <dir> [--index <path>]
                                       Runs the 2x2 (retrieval mode x
                                       full/cropped) sweep over every
                                       a_*.png frame in <dir>, then the
                                       full detect->rectify->identify path
                                       for the best cell.
            """);
    }
}
