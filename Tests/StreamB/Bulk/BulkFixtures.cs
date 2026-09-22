using System.Runtime.CompilerServices;

namespace LoreFetch.Tests.StreamB.Bulk;

/// Locates the committed synthetic JSONL sample by the calling test
/// file's own source path rather than the test run's output directory --
/// the fixture is a source file, not a build output, and this project's
/// .csproj is frozen so no "copy to output directory" item can be added
/// for it.
internal static class BulkFixtures
{
    public static string UniqueArtworkSamplePath(
        [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "synthetic-unique-artwork.jsonl");
}
