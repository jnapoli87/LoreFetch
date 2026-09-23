// LoreFetch.Lab — index-build tooling entry point.
//
// Hand-rolled dispatch on purpose: the .csproj is frozen and cannot take a
// new command-line-parsing package, and a small switch does not need one.
// Structured so each command adds one case without touching this shape.
using LoreFetch.Lab;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var rest = args[1..];

return await (command switch
{
    "bulk" => BulkCommand.RunAsync(rest),
    "printings" => PrintingsCommand.RunAsync(rest),
    "images" => ImagesCommand.RunAsync(rest),
    "build-index" => BuildIndexCommand.RunAsync(rest),
    "detect" => DetectCommand.RunAsync(rest),
    "identify" => IdentifyCommand.RunAsync(rest),
    "synth" => SynthCommand.RunAsync(rest),
    "round-trip-gate" => RoundTripGateCommand.RunAsync(rest),
    "query-hash-witness" => QueryHashWitnessCommand.RunAsync(rest),
    "crop-scale" => CropScaleCommand.RunAsync(rest),
    "retrieval-experiment" => RetrievalExperimentCommand.RunAsync(rest),
    "accuracy" => AccuracyCommand.RunAsync(rest),
    "expand-experiment" => ExpandExperimentCommand.RunAsync(rest),
    _ => Unknown(command),
});

static Task<int> Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return Task.FromResult(1);
}

static void PrintUsage()
{
    Console.WriteLine("""
        LoreFetch.Lab -- maintainer index-build tooling.

        Usage:
          bulk [--out <dir>]      Download unique_artwork, run the filter cascade,
                                   write the surviving arts to a manifest.
                                   Default out dir: <repo>/scryfall-bulk/ (gitignored).
          printings [--out <dir>] Report the fraction of in-scope artworks that have
                                   exactly one in-scope printing.
          images [--manifest <path>] --cache <dir> [--concurrency N] [--limit N]
                                   Download the bulk manifest's image_uris.normal
                                   renders into an external, gitignored cache,
                                   keyed by ArtworkId. Resumable; skips files
                                   already present; --cache must resolve outside
                                   the repository.
          build-index [--manifest <path>] --cache <dir> --out <file.lfidx>
                                   [--subset N] [--parallelism N] [--allow-missing]
                                   [--ids <file>] [--fill N]
                                   Hash every cached image_uris.normal render
                                   (ReferenceTransform -> CardHasher) into a
                                   cards.lfidx. --subset N or --ids <file>
                                   (one ArtworkId per line, optionally padded
                                   with --fill N more) build a smaller,
                                   labelled index instead of the full one.
          detect <image> [--out <png>] [--max N]
          detect --all <dir> [--max N]
                                   Run ContourCardDetector against a still
                                   image (or every image in a folder) and
                                   write an annotated PNG: accepted quads in
                                   green with corner labels, rejected
                                   contours in orange labelled with their
                                   discard reason, plus a rectified 488x680
                                   crop per accepted quad. Output defaults to
                                   C:\LoreFetchData\detect-out\ and is
                                   refused inside the repository -- an input
                                   or output image may show real card
                                   artwork.
          identify <image> [--index <path>] [--max N] [--top N]
                                   Run detect -> rectify -> Identify against
                                   a real hash index (default
                                   data/index/cards.lfidx) and print the top
                                   N candidates per detected card, plus a
                                   180-degree-flip sanity check.
          synth card <image> [--height N] [--out <png>] [--seed N]
                      [--keystone F] [--blur F] [--noise F] [--quality N]
                                   Build a camera-like frame from a source
                                   card render: downscale, keystone, blur,
                                   sensor noise, JPEG artefacts.
          synth mat --contrast light|mid|dark [--out <png>] [--seed N]
                     [--noise F] [--seam]
                                   Build a card-free mat frame at the given
                                   contrast (Risk 3).
          round-trip-gate --cache <dir> [--index <path>]
                           [--out <thresholds.json>] [--lands N]
                           [--non-lands N] [--seed N] [--min-rank1-rate F]
                                   Package B2: sample artworks from the
                                   committed index, run each cached render
                                   through Identify, report the rank-1
                                   ArtworkId match rate and the distance/
                                   margin distributions. --out writes
                                   referenceFloor + margin stats into a
                                   thresholds.json.
          query-hash-witness --cache <dir> [--index <path>] --out <path>
                              [--sample-size N] [--seed N]
                                   Package B2: writes a fixed sample of
                                   query-side hashes for later
                                   cross-architecture comparison.
          crop-scale --cache <dir> [--index <path>] [--sample N] [--seed N]
                                   Package B5c: sweeps a synthetic crop-
                                   scale error over sampled non-land
                                   renders and reports the rank-1 rate and
                                   distance/margin distributions per level.
          retrieval-experiment dump-contours <dir>
          retrieval-experiment run <dir> [--index <path>]
                                   H1/H2/H3 real-frame investigation: the
                                   RETR_EXTERNAL/RETR_LIST x full/cropped
                                   2x2 sweep over test-images/a_corpus/,
                                   plus the identify pass on the best
                                   cell. See docs/accuracy.md.
          accuracy [--index <path>] [--ok-distance N] [--max-wrong N]
                   [--images-root <checkout>]
                                   Package B6: runs the accuracy harness
                                   against whatever subset of
                                   test-images/ground-truth.csv +
                                   test-images/fixtures/ exists on disk.
                                   Reports correct@1/wrong@1/no-match per
                                   height and per rung, the margin
                                   distribution, and the lands-excluded
                                   count. Never writes thresholds.json.
                                   --images-root points a worktree build at
                                   a different checkout's test-images/.
          expand-experiment [--images-root <dir>] [--index <path>]
                             [--cache <dir>] [--file-prefix <prefix>]
                             [--expand-w F] [--expand-h F] [--ok-distance N]
                             [--border-sample N] [--seed N] [--top N]
                                   Package E1a: per ground-truth slot,
                                   compares top-1 distance/correctness at
                                   factor 1.0 vs. a quad expanded about its
                                   own centroid (measured from real border
                                   ratios, or given explicitly) vs. a dual
                                   (best-of-both) hypothesis.
        """);
}
