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
        """);
}
