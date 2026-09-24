namespace LoreFetch.Lab.Images;

/// Refuses to run the image download against a cache directory that
/// resolves inside the repository (or a linked worktree of it). The
/// image cache holds Scryfall artwork -- WotC IP that `DECISIONS.md` says
/// must never be committed -- so a cache path under source control is a
/// mistake worth stopping the run for, not a warning.
public static class CacheDirectoryGuard
{
    /// Throws when `cacheDir` is the repo root or any path under it.
    /// `repoRoot` is passed in rather than located here so this stays a
    /// pure function over two paths -- easy to unit test without relying
    /// on where the test binary itself happens to sit.
    public static void EnsureOutsideRepo(string cacheDir, string repoRoot)
    {
        var fullCache = NormalizeDir(Path.GetFullPath(cacheDir));
        var fullRepo = NormalizeDir(Path.GetFullPath(repoRoot));

        var isInside = fullCache.Equals(fullRepo, StringComparison.OrdinalIgnoreCase)
            || fullCache.StartsWith(fullRepo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (isInside)
        {
            throw new InvalidOperationException(
                $"Refusing to use cache directory \"{cacheDir}\" -- it resolves inside the " +
                $"repository (\"{repoRoot}\"). Scryfall artwork must never land where git " +
                "could see it. Pass a --cache path outside the repository, e.g. " +
                "C:\\LoreFetchData\\scryfall-cache.");
        }
    }

    private static string NormalizeDir(string path) => path.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);
}
