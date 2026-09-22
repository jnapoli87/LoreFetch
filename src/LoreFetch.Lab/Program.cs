// LoreFetch.Lab — index-build tooling entry point.
//
// Hand-rolled dispatch on purpose: the .csproj is frozen and cannot take a
// new command-line-parsing package, and a two-command switch does not
// need one. Structured so B4b (`images`) and B4c (`build-index`) add a
// case each without touching this shape.
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
        """);
}
