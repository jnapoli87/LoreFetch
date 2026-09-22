using LoreFetch.Lab.Synthetic;
using OpenCvSharp;

namespace LoreFetch.Lab;

/// `lab synth card <source-image> [--height N] [--out <png>] [--seed N]
///   [--keystone F] [--blur F] [--noise F] [--quality N]`
/// `lab synth mat --contrast light|mid|dark [--out <png>] [--seed N]
///   [--noise F] [--seam]`
///
/// Package B7: writes a `SyntheticFrameGenerator`/`BareMatGenerator` output
/// to disk so it can be looked at, the same role `detect` plays for
/// `ContourCardDetector`. `card`'s source image may be a real Scryfall
/// render (a user's own local cache) or a procedural stand-in; either way
/// the WRITTEN frame may end up showing recognizable card artwork, so the
/// same never-write-inside-the-repo rule `DetectCommand` enforces applies
/// here verbatim (CLAUDE.md "Never commit card imagery").
public static class SynthCommand
{
    public const string DefaultOutDir = @"C:\LoreFetchData\synth-out";

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
            "card" => RunCard(rest),
            "mat" => RunMat(rest),
            _ => Unknown(sub),
        };
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"synth: unknown sub-command \"{sub}\".");
        PrintUsage();
        return 1;
    }

    private static int RunCard(string[] args)
    {
        CardArgs parsed;
        try
        {
            parsed = ParseCardArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        if (!File.Exists(parsed.ImagePath))
        {
            Console.Error.WriteLine($"synth card: image not found at \"{parsed.ImagePath}\".");
            return 1;
        }

        var outPath = parsed.OutPath ?? DefaultOutputPath(parsed.ImagePath);
        if (!TryEnsureOutputOutsideRepo(outPath))
        {
            return 1;
        }

        using var source = Cv2.ImRead(parsed.ImagePath, ImreadModes.Color);
        if (source.Empty())
        {
            Console.Error.WriteLine($"synth card: could not decode \"{parsed.ImagePath}\" as an image.");
            return 1;
        }

        var options = new SyntheticFrameOptions
        {
            Seed = parsed.Seed,
            KeystoneAmount = parsed.Keystone,
            BlurSigma = parsed.Blur,
            NoiseSigma = parsed.Noise,
            JpegQuality = parsed.Quality,
        };

        SyntheticFrameResult result;
        try
        {
            result = SyntheticFrameGenerator.Generate(source, parsed.HeightInches, options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"synth card: {ex.Message}");
            return 1;
        }

        using (result.Frame)
        {
            using var color = Core.Imaging.FrameMat.ToMat(result.Frame);
            WriteImage(outPath, color);
        }

        Console.WriteLine(
            $"synth card: wrote {outPath} -- card ~{result.ExpectedCardWidthPx:F0}x{result.ExpectedCardHeightPx:F0}px " +
            $"at {parsed.HeightInches}in.");
        return 0;
    }

    private static int RunMat(string[] args)
    {
        MatArgs parsed;
        try
        {
            parsed = ParseMatArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        var outPath = parsed.OutPath ?? Path.Combine(DefaultOutDir, $"mat-{parsed.Contrast.ToString().ToLowerInvariant()}.png");
        if (!TryEnsureOutputOutsideRepo(outPath))
        {
            return 1;
        }

        var options = new BareMatOptions { Seed = parsed.Seed, NoiseSigma = parsed.Noise, WithSeam = parsed.Seam };
        using var frame = BareMatGenerator.Generate(parsed.Contrast, options);
        using var color = Core.Imaging.FrameMat.ToMat(frame);
        WriteImage(outPath, color);

        Console.WriteLine($"synth mat: wrote {outPath} -- contrast={parsed.Contrast}.");
        return 0;
    }

    private static void WriteImage(string outPath, Mat color)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        Cv2.ImWrite(outPath, color);
    }

    /// Mirrors `DetectCommand.EnsureOutputOutsideRepo`'s logic and its own
    /// reason for not sharing code with `Images.CacheDirectoryGuard`
    /// (that guard's message is about the Scryfall cache specifically) --
    /// this command's own generated frames are a third, distinct kind of
    /// never-commit artifact (a SYNTHETIC frame that may still show
    /// recognizable card art, if the caller pointed `card` at a real
    /// render) and deserves its own message rather than a generic one.
    /// Returns false (having already printed the reason, whichever it is)
    /// when the caller should stop; true when it is safe to proceed --
    /// including when the repo root cannot be located at all, in which
    /// case the check is skipped with a note, the same non-fatal fallback
    /// `DetectCommand` uses.
    private static bool TryEnsureOutputOutsideRepo(string outPath)
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            Console.WriteLine("synth: could not locate the repository automatically -- skipping the inside-repo output check.");
            return true;
        }

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        var fullOut = NormalizeDir(string.IsNullOrEmpty(outDir) ? Path.GetFullPath(outPath) : outDir);
        var fullRepo = NormalizeDir(Path.GetFullPath(repoRoot!));

        var isInside = fullOut.Equals(fullRepo, StringComparison.OrdinalIgnoreCase)
            || fullOut.StartsWith(fullRepo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!isInside)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"synth: refusing to write \"{outPath}\" -- it resolves inside the repository (\"{repoRoot}\"). " +
            "A generated frame may show recognizable card artwork and must never land where git could see it. " +
            $"Pass an --out path outside the repository, e.g. {DefaultOutDir}.");
        return false;
    }

    private static string NormalizeDir(string path) => path.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);

    private static string DefaultOutputPath(string imagePath) =>
        Path.Combine(DefaultOutDir, Path.GetFileNameWithoutExtension(imagePath) + ".synth.png");

    private sealed record CardArgs(
        string ImagePath, float HeightInches, string? OutPath, int Seed, float Keystone, double Blur, double Noise, int Quality);

    private sealed record MatArgs(MatContrast Contrast, string? OutPath, int Seed, double Noise, bool Seam);

    private static CardArgs ParseCardArgs(string[] args)
    {
        string? imagePath = null;
        var height = 9.75f;
        string? outPath = null;
        var defaults = SyntheticFrameOptions.Default;
        var seed = defaults.Seed;
        var keystone = defaults.KeystoneAmount;
        var blur = defaults.BlurSigma;
        var noise = defaults.NoiseSigma;
        var quality = defaults.JpegQuality;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--height" when i + 1 < args.Length:
                    if (!float.TryParse(args[++i], out height) || height <= 0)
                    {
                        throw new ArgumentException("synth card: --height must be a positive number.");
                    }

                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("synth card: --seed must be an integer.");
                    }

                    break;
                case "--keystone" when i + 1 < args.Length:
                    if (!float.TryParse(args[++i], out keystone))
                    {
                        throw new ArgumentException("synth card: --keystone must be a number.");
                    }

                    break;
                case "--blur" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out blur))
                    {
                        throw new ArgumentException("synth card: --blur must be a number.");
                    }

                    break;
                case "--noise" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out noise))
                    {
                        throw new ArgumentException("synth card: --noise must be a number.");
                    }

                    break;
                case "--quality" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out quality) || quality is < 0 or > 100)
                    {
                        throw new ArgumentException("synth card: --quality must be between 0 and 100.");
                    }

                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"synth card: unrecognised argument \"{args[i]}\".");
                    }

                    if (imagePath is not null)
                    {
                        throw new ArgumentException("synth card: only one image path may be given.");
                    }

                    imagePath = args[i];
                    break;
            }
        }

        if (imagePath is null)
        {
            throw new ArgumentException("synth card: pass a source image path.");
        }

        return new CardArgs(imagePath, height, outPath, seed, keystone, blur, noise, quality);
    }

    private static MatArgs ParseMatArgs(string[] args)
    {
        MatContrast? contrast = null;
        string? outPath = null;
        var defaults = BareMatOptions.Default;
        var seed = defaults.Seed;
        var noise = defaults.NoiseSigma;
        var seam = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--contrast" when i + 1 < args.Length:
                    contrast = ParseContrast(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seed))
                    {
                        throw new ArgumentException("synth mat: --seed must be an integer.");
                    }

                    break;
                case "--noise" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out noise))
                    {
                        throw new ArgumentException("synth mat: --noise must be a number.");
                    }

                    break;
                case "--seam":
                    seam = true;
                    break;
                default:
                    throw new ArgumentException($"synth mat: unrecognised argument \"{args[i]}\".");
            }
        }

        if (contrast is null)
        {
            throw new ArgumentException("synth mat: pass --contrast light|mid|dark.");
        }

        return new MatArgs(contrast.Value, outPath, seed, noise, seam);
    }

    private static MatContrast ParseContrast(string value) => value.ToLowerInvariant() switch
    {
        "light" => MatContrast.Light,
        "mid" => MatContrast.Mid,
        "dark" => MatContrast.Dark,
        _ => throw new ArgumentException($"synth mat: unknown contrast \"{value}\" -- expected light, mid or dark."),
    };

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            Usage:
              synth card <image> [--height N] [--out <png>] [--seed N]
                          [--keystone F] [--blur F] [--noise F] [--quality N]
                                       Runs SyntheticFrameGenerator against a source
                                       card render and writes the resulting camera-
                                       like frame. --height defaults to 9.75 (the
                                       3x3-grid mount height).
              synth mat --contrast light|mid|dark [--out <png>] [--seed N]
                        [--noise F] [--seam]
                                       Runs BareMatGenerator and writes a card-free
                                       mat frame at the given contrast.
            Default output directory: {DefaultOutDir} (never inside the repo).
            """);
    }
}
