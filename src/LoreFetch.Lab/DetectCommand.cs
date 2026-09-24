using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace LoreFetch.Lab;

/// `lab detect <image> [--out <png>] [--max N]` or
/// `lab detect --all <dir> [--max N]`
///
/// Runs `ContourCardDetector` (package B5a) against a still image (or every
/// image in a folder) and draws both halves of `DetectWithDiagnostics` onto
/// a PNG: accepted quads in green with TL/TR/BR/BL corner labels, and every
/// rejected contour big enough to be worth ink in orange, labelled with its
/// discard reason (tiny noise contours below
/// `ContourDetectorOptions.DiagnosticNoiseFloorPx` are still counted in the
/// printed summary but never drawn). For every ACCEPTED quad, also writes
/// the `PerspectiveRectifier`-rectified 488x680 crop next to the overlay,
/// as `<name>.card<i>.png` (package B5b) -- this is what lets B5a's
/// "Plains on black" crop-scale finding actually be looked at, rather than
/// eyeballed off the overlay's quad outline alone. Output never lands in
/// the repository -- see `EnsureOutputOutsideRepo` -- because the input
/// image, and therefore the annotated output and the rectified crops, may
/// itself contain photographed card artwork (DECISIONS.md "Never commit card
/// imagery").
public static class DetectCommand
{
    /// DECISIONS.md/the package brief: detection output goes outside the repo
    /// tree entirely, on this Windows-only tool.
    public const string DefaultOutDir = @"C:\LoreFetchData\detect-out";

    /// Matches `ScanPipeline.MaxDetectionCards` (the 3x3 grid ceiling) --
    /// duplicated as a literal rather than referenced, so this Lab tool has
    /// no compile-time dependency on `Core.Scanning`'s wiring type just for
    /// one constant.
    private const int DefaultMaxCards = 9;

    private static readonly Scalar AcceptedColor = new(0, 200, 0); // green, BGR
    private static readonly Scalar RejectedColor = new(0, 140, 255); // orange, BGR
    private static readonly Scalar TextColor = new(255, 255, 255);

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        DetectArgs parsed;
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

        var repoRootFound = RepoPaths.TryFindRepoRoot(out var repoRoot);

        if (parsed.OutPath is not null)
        {
            if (repoRootFound)
            {
                try
                {
                    EnsureOutputOutsideRepo(parsed.OutPath, repoRoot!);
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }
            else
            {
                Console.WriteLine(
                    "detect: could not locate the repository automatically -- skipping the inside-repo output check.");
            }
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug)
            .AddSimpleConsole(o => o.SingleLine = true));
        var detector = new ContourCardDetector(loggerFactory.CreateLogger<ContourCardDetector>());

        try
        {
            return parsed.AllDir is not null
                ? RunAll(detector, parsed.AllDir, parsed.MaxCards)
                : RunOne(detector, parsed.ImagePath!, parsed.OutPath, parsed.MaxCards);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunOne(ContourCardDetector detector, string imagePath, string? outPath, int maxCards)
    {
        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine($"detect: image not found at \"{imagePath}\".");
            return 1;
        }

        var resolvedOut = outPath ?? DefaultOutputPath(imagePath);
        var (accepted, rejected, cropPaths) = ProcessOne(detector, imagePath, resolvedOut, maxCards);

        Console.WriteLine(Summarize(Path.GetFileName(imagePath), accepted, rejected));
        Console.WriteLine($"  -> {resolvedOut}");
        foreach (var cropPath in cropPaths)
        {
            Console.WriteLine($"  -> {cropPath}");
        }

        return 0;
    }

    private static int RunAll(ContourCardDetector detector, string dir, int maxCards)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"detect: directory not found at \"{dir}\".");
            return 1;
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };
        var files = Directory.EnumerateFiles(dir)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"detect: no images found in \"{dir}\".");
            return 0;
        }

        Directory.CreateDirectory(DefaultOutDir);
        var totalCrops = 0;

        foreach (var file in files)
        {
            var outPath = DefaultOutputPath(file);
            var (accepted, rejected, cropPaths) = ProcessOne(detector, file, outPath, maxCards);
            Console.WriteLine(Summarize(Path.GetFileName(file), accepted, rejected));
            foreach (var cropPath in cropPaths)
            {
                Console.WriteLine($"  -> {cropPath}");
            }

            totalCrops += cropPaths.Count;
        }

        Console.WriteLine($"Wrote {files.Count} annotated frame(s) and {totalCrops} rectified crop(s) to {DefaultOutDir}");
        return 0;
    }

    private static (IReadOnlyList<CardQuad> Accepted, IReadOnlyList<RejectedContour> Rejected, IReadOnlyList<string> CropPaths) ProcessOne(
        ContourCardDetector detector, string imagePath, string outPath, int maxCards)
    {
        using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (color.Empty())
        {
            throw new InvalidOperationException($"detect: could not decode \"{imagePath}\" as an image.");
        }

        using var frame = FrameMat.FromMat(color);
        var diagnostics = detector.DetectWithDiagnostics(frame, maxCards);

        Draw(color, diagnostics);

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        Cv2.ImWrite(outPath, color);

        // For every ACCEPTED quad -- never the rejected ones, which are not
        // real cards -- also write the rectified 488x680 crop next to the
        // overlay: package B5b. This is what B5a's "Plains on black" finding
        // (the accepted quad sits on the black border's INNER edge, not the
        // card's outer edge) needs a crop to look at directly, and what
        // package B5c's crop-scale sweep will compare between mats.
        var rectifier = new PerspectiveRectifier();
        var cropPaths = new List<string>(diagnostics.Accepted.Count);
        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        for (var i = 0; i < diagnostics.Accepted.Count; i++)
        {
            var card = rectifier.Rectify(frame, diagnostics.Accepted[i]);
            using var cardMat = QueryTransform.ToMat(card);
            var cropPath = Path.Combine(outDir ?? DefaultOutDir, $"{baseName}.card{i}.png");
            Cv2.ImWrite(cropPath, cardMat);
            cropPaths.Add(cropPath);
        }

        return (diagnostics.Accepted, diagnostics.Rejected, cropPaths);
    }

    private static void Draw(Mat color, DetectionDiagnostics diagnostics)
    {
        foreach (var quad in diagnostics.Accepted)
        {
            DrawClosedPolyline(color, [quad.TL, quad.TR, quad.BR, quad.BL], AcceptedColor, thickness: 2);
            Label(color, quad.TL, "TL", AcceptedColor);
            Label(color, quad.TR, "TR", AcceptedColor);
            Label(color, quad.BR, "BR", AcceptedColor);
            Label(color, quad.BL, "BL", AcceptedColor);
        }

        foreach (var rejection in diagnostics.Rejected)
        {
            if (rejection.AreaPx < ContourDetectorOptions.Default.DiagnosticNoiseFloorPx || rejection.Points.Count < 2)
            {
                continue; // counted in the summary, but not worth ink -- pure noise
            }

            DrawClosedPolyline(color, rejection.Points, RejectedColor, thickness: 1);
            Label(color, Centroid(rejection.Points), rejection.Reason.ToString(), RejectedColor);
        }
    }

    private static void DrawClosedPolyline(Mat img, IReadOnlyList<PointF2> points, Scalar color, int thickness)
    {
        var pts = points.Select(ToPoint).ToArray();
        Cv2.Polylines(img, [pts], isClosed: true, color, thickness, LineTypes.AntiAlias);
    }

    /// A white outline stroke behind the coloured fill stroke, so a label
    /// reads clearly whether it lands on a light or dark part of the frame.
    private static void Label(Mat img, PointF2 at, string text, Scalar color)
    {
        var point = ToPoint(at);
        Cv2.PutText(img, text, point, HersheyFonts.HersheySimplex, 0.4, TextColor, thickness: 2, LineTypes.AntiAlias);
        Cv2.PutText(img, text, point, HersheyFonts.HersheySimplex, 0.4, color, thickness: 1, LineTypes.AntiAlias);
    }

    private static Point ToPoint(PointF2 p) => new((int)MathF.Round(p.X), (int)MathF.Round(p.Y));

    private static PointF2 Centroid(IReadOnlyList<PointF2> points)
    {
        var x = points.Average(p => p.X);
        var y = points.Average(p => p.Y);
        return new PointF2(x, y);
    }

    private static string Summarize(string fileName, IReadOnlyList<CardQuad> accepted, IReadOnlyList<RejectedContour> rejected)
    {
        var reasonCounts = rejected
            .GroupBy(r => r.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}");
        var reasonsText = rejected.Count > 0 ? string.Join(", ", reasonCounts) : "none";

        var sizesText = accepted.Count > 0
            ? string.Join(", ", accepted.Select(q => $"{ApproxWidth(q):F0}x{ApproxHeight(q):F0}"))
            : "-";

        return $"{fileName}: accepted={accepted.Count} [{sizesText}] rejects: {reasonsText}";
    }

    private static float ApproxWidth(CardQuad q) => (Distance(q.TL, q.TR) + Distance(q.BL, q.BR)) / 2f;

    private static float ApproxHeight(CardQuad q) => (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f;

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static string DefaultOutputPath(string imagePath) =>
        Path.Combine(DefaultOutDir, Path.GetFileNameWithoutExtension(imagePath) + ".detect.png");

    /// Mirrors `Images.CacheDirectoryGuard.EnsureOutsideRepo`'s logic but
    /// keeps its own message: an annotated detection frame is a different
    /// kind of never-commit artifact (a photograph that may show a real
    /// card) than the Scryfall image cache that guard was written for, and
    /// a shared helper would have to speak generically about both, which
    /// serves neither message well.
    private static void EnsureOutputOutsideRepo(string outPath, string repoRoot)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        var fullOut = NormalizeDir(string.IsNullOrEmpty(outDir) ? Path.GetFullPath(outPath) : outDir);
        var fullRepo = NormalizeDir(Path.GetFullPath(repoRoot));

        var isInside = fullOut.Equals(fullRepo, StringComparison.OrdinalIgnoreCase)
            || fullOut.StartsWith(fullRepo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (isInside)
        {
            throw new InvalidOperationException(
                $"detect: refusing to write \"{outPath}\" -- it resolves inside the repository (\"{repoRoot}\"). " +
                "A detection frame may itself show photographed card artwork and must never land where git could " +
                $"see it. Pass an --out path outside the repository, e.g. {DefaultOutDir}.");
        }
    }

    private static string NormalizeDir(string path) => path.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);

    private static DetectArgs ParseArgs(string[] args)
    {
        string? imagePath = null;
        string? allDir = null;
        string? outPath = null;
        var maxCards = DefaultMaxCards;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all" when i + 1 < args.Length:
                    allDir = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--max" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxCards) || maxCards < 1)
                    {
                        throw new ArgumentException("detect: --max must be a positive integer.");
                    }

                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"detect: unrecognised argument \"{args[i]}\".");
                    }

                    if (imagePath is not null)
                    {
                        throw new ArgumentException("detect: only one image path may be given.");
                    }

                    imagePath = args[i];
                    break;
            }
        }

        if (imagePath is null && allDir is null)
        {
            throw new ArgumentException("detect: pass an image path, or --all <dir>.");
        }

        if (imagePath is not null && allDir is not null)
        {
            throw new ArgumentException("detect: pass either an image path or --all <dir>, not both.");
        }

        if (outPath is not null && allDir is not null)
        {
            throw new ArgumentException(
                "detect: --out applies to a single image only; --all always writes to the default output directory.");
        }

        return new DetectArgs(imagePath, allDir, outPath, maxCards);
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage:
              detect <image> [--out <png>] [--max N]   Run the detector on one image.
              detect --all <dir> [--max N]             Run it on every image in a folder.
            Writes an annotated overlay PNG plus one rectified 488x680 crop
            per accepted quad (<name>.card<i>.png).
            Default output directory: {DefaultOutDir} (never inside the repo).
            """);
    }

    private sealed record DetectArgs(string? ImagePath, string? AllDir, string? OutPath, int MaxCards);
}
