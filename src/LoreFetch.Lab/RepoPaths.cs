namespace LoreFetch.Lab;

/// Locates paths relative to the repository root rather than the process's
/// current working directory, so `bulk`/`printings` default to the same
/// place (`<repo>/scryfall-bulk/`, gitignored) whether invoked via
/// `dotnet run` from the project folder or via a built exe from anywhere.
public static class RepoPaths
{
    private const string SolutionFileName = "LoreFetch.slnx";

    /// Walks up from the running executable's own directory looking for
    /// the solution file. Throws rather than silently falling back to the
    /// current directory -- a maintainer tool downloading gigabytes to the
    /// wrong place is worse than a clear failure.
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName} above {AppContext.BaseDirectory}. " +
            "Pass --out explicitly if running outside the repository checkout.");
    }

    public static string DefaultScryfallBulkDir() => Path.Combine(FindRepoRoot(), "scryfall-bulk");
}
