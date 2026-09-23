using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Core.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Lab;

/// `lab identify <image> [--index <path>] [--max N] [--top N]`
///
/// Package B5b's "early real-path smoke check" (a user ruling): the FULL
/// shipping query path -- `ContourCardDetector.Detect` ->
/// `PerspectiveRectifier.Rectify` -> `HashCardIdentifier.Identify` -- run
/// against a still image and the real committed index, printed as a
/// ranked candidate table per detected card. This exists so the stream can
/// look at real accuracy numbers (and specifically Sol Ring's matched art)
/// well before B6's full accuracy harness, without waiting on B2/B6 to be
/// built. It deliberately reuses `ContourCardDetector` exactly as `detect`
/// does and, since package DH, `Core.Scanning.DualHypothesisIdentification`
/// for the rectify+identify step -- exactly the same as-detected/border-
/// expanded decision `ScanPipeline` makes -- so this command's printed
/// distances match what the app would actually produce.
public static class IdentifyCommand
{
    private const int DefaultMaxCards = 9; // matches DetectCommand's own default -- the 3x3 grid ceiling
    private const int DefaultTopN = 5;

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        IdentifyArgs parsed;
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

        if (!File.Exists(parsed.ImagePath))
        {
            Console.Error.WriteLine($"identify: image not found at \"{parsed.ImagePath}\".");
            return 1;
        }

        var indexPath = parsed.IndexPath;
        if (indexPath is null)
        {
            // Non-throwing lookup, same pattern as build-index/detect: whether
            // the repo can be located decides what this command can DEFAULT,
            // not whether it can run at all.
            if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
            {
                Console.Error.WriteLine("identify: could not locate the repository automatically. Pass --index explicitly.");
                return 1;
            }

            indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        }

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine(
                $"identify: index not found at \"{indexPath}\". Run \"lab build-index\" first, or pass --index explicitly.");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning)
            .AddSimpleConsole(o => o.SingleLine = true));

        HashCardIdentifier identifier;
        try
        {
            identifier = HashCardIdentifier.Load(indexPath, loggerFactory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"identify: failed to load index \"{indexPath}\": {ex.Message}");
            return 1;
        }

        using var color = Cv2.ImRead(parsed.ImagePath, ImreadModes.Color);
        if (color.Empty())
        {
            Console.Error.WriteLine($"identify: could not decode \"{parsed.ImagePath}\" as an image.");
            return 1;
        }

        using var frame = FrameMat.FromMat(color);
        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var detected = detector.Detect(frame, parsed.MaxCards);

        Console.WriteLine($"{Path.GetFileName(parsed.ImagePath)}: detected {detected.Count} card(s). Index: {indexPath}");

        var rectifier = new PerspectiveRectifier();
        for (var i = 0; i < detected.Count; i++)
        {
            // Package DH: the same dual-hypothesis decision ScanPipeline
            // and the accuracy harness make -- as-detected vs. border-
            // expanded, keep the lower top-1 distance -- so this command's
            // numbers match what the app would actually produce, not a
            // single-hypothesis approximation of it.
            var outcome = DualHypothesisIdentification.Identify(frame, detected[i], rectifier, identifier, parsed.TopN);
            var card = outcome.Image;
            var candidates = outcome.Candidates;

            Console.WriteLine();
            Console.WriteLine(
                $"Card {i}: (dual-hypothesis winner={outcome.Winner}" +
                (outcome.ExpandedHypothesisSkipped ? ", expanded hypothesis skipped -- would leave the frame)" : ")"));
            for (var rank = 0; rank < candidates.Count; rank++)
            {
                var c = candidates[rank];
                Console.WriteLine($"  #{rank + 1}: {c.OracleName} (ArtworkId={c.ArtworkId ?? "-"}, Distance={c.Distance})");
            }

            if (candidates.Count == 0)
            {
                Console.WriteLine("  (no candidates -- empty index?)");
                continue;
            }

            // HashCardIdentifier.Identify already hashes BOTH orientations
            // per candidate and keeps the better distance -- see its own
            // doc comment. Feeding it a manually 180-degree-rotated copy of
            // the SAME rectified card exercises exactly the same internal
            // logic from the other side and must land on the identical
            // best-of-both-orientations answer, since flipping the query
            // only swaps which of the two hashes Identify computes first.
            // Printed explicitly because the task calls for seeing the
            // flip handling's own result, not just trusting the doc
            // comment that describes it.
            var flippedCard = Flip180(card);
            var flippedCandidates = identifier.Identify(flippedCard, parsed.TopN);
            var top = candidates[0];
            var flippedTop = flippedCandidates.Count > 0 ? flippedCandidates[0] : (CardCandidate?)null;
            var matches = flippedTop is not null
                && flippedTop.Value.OracleId == top.OracleId
                && flippedTop.Value.Distance == top.Distance;

            Console.WriteLine(
                $"  180-flip check: upright top1 distance={top.Distance} vs " +
                $"pre-flipped-query top1 distance={flippedTop?.Distance.ToString() ?? "-"} " +
                $"({(matches ? "match -- orientation-invariant, as expected" : "MISMATCH -- investigate")})");
        }

        return 0;
    }

    /// Builds a fresh `RectifiedCard` that is `card` rotated 180 degrees
    /// (`Cv2.Flip(..., FlipMode.XY)`, the same call `HashCardIdentifier`
    /// makes internally) -- used only to demonstrate the identifier's own
    /// orientation handling from the command line; production code never
    /// needs this, `HashCardIdentifier.Identify` already does it per query.
    private static RectifiedCard Flip180(RectifiedCard card)
    {
        using var mat = QueryTransform.ToMat(card);
        using var flipped = new Mat();
        Cv2.Flip(mat, flipped, FlipMode.XY);

        var bytesPerPixel = card.Layout == PixelLayout.Bgra32 ? 4 : 3;
        var stride = RectifiedCard.CanonicalWidth * bytesPerPixel;
        var pixels = new byte[stride * RectifiedCard.CanonicalHeight];
        for (var row = 0; row < RectifiedCard.CanonicalHeight; row++)
        {
            Marshal.Copy(flipped.Ptr(row), pixels, row * stride, stride);
        }

        return new RectifiedCard(pixels, stride, card.Layout, card.SourceQuad);
    }

    private sealed record IdentifyArgs(string ImagePath, string? IndexPath, int MaxCards, int TopN);

    private static IdentifyArgs ParseArgs(string[] args)
    {
        string? imagePath = null;
        string? indexPath = null;
        var maxCards = DefaultMaxCards;
        var topN = DefaultTopN;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length:
                    indexPath = args[++i];
                    break;
                case "--max" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxCards) || maxCards < 1)
                    {
                        throw new ArgumentException("identify: --max must be a positive integer.");
                    }

                    break;
                case "--top" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out topN) || topN < 1)
                    {
                        throw new ArgumentException("identify: --top must be a positive integer.");
                    }

                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"identify: unrecognised argument \"{args[i]}\".");
                    }

                    if (imagePath is not null)
                    {
                        throw new ArgumentException("identify: only one image path may be given.");
                    }

                    imagePath = args[i];
                    break;
            }
        }

        if (imagePath is null)
        {
            throw new ArgumentException("identify: pass an image path.");
        }

        return new IdentifyArgs(imagePath, indexPath, maxCards, topN);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              identify <image> [--index <path>] [--max N] [--top N]

            Runs detect -> rectify -> HashCardIdentifier.Identify against a
            real hash index and prints the top --top (default 5) candidates
            per detected card: oracle name, ArtworkId, Hamming distance --
            plus a 180-degree-flip sanity check per card.
            Default index: <repo>/data/index/cards.lfidx.
            """);
    }
}
