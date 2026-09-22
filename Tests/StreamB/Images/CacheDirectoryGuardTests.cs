using LoreFetch.Lab.Images;
using Xunit;

namespace LoreFetch.Tests.StreamB.Images;

/// `EnsureOutsideRepo` is the guard against ever pointing the image cache
/// at a path git could see -- the cache holds Scryfall artwork, which
/// CLAUDE.md forbids committing in any form.
public class CacheDirectoryGuardTests
{
    private static readonly string RepoRoot = Path.Combine("C:" + Path.DirectorySeparatorChar, "Repos", "LoreFetch");

    [Fact]
    public void Throws_WhenCacheDirIsExactlyTheRepoRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CacheDirectoryGuard.EnsureOutsideRepo(RepoRoot, RepoRoot));
    }

    [Fact]
    public void Throws_WhenCacheDirIsNestedInsideTheRepo()
    {
        var cacheDir = Path.Combine(RepoRoot, "scryfall-cache");

        Assert.Throws<InvalidOperationException>(() =>
            CacheDirectoryGuard.EnsureOutsideRepo(cacheDir, RepoRoot));
    }

    [Fact]
    public void Throws_WhenCacheDirIsDeeplyNestedInsideTheRepo()
    {
        var cacheDir = Path.Combine(RepoRoot, ".claude", "worktrees", "stream-b", "scryfall-cache");

        Assert.Throws<InvalidOperationException>(() =>
            CacheDirectoryGuard.EnsureOutsideRepo(cacheDir, RepoRoot));
    }

    [Fact]
    public void DoesNotThrow_WhenCacheDirIsOutsideTheRepo()
    {
        var cacheDir = Path.Combine("C:" + Path.DirectorySeparatorChar, "LoreFetchData", "scryfall-cache");

        CacheDirectoryGuard.EnsureOutsideRepo(cacheDir, RepoRoot); // must not throw
    }

    [Fact]
    public void DoesNotThrow_ForASiblingDirectoryWhoseNameMerelyStartsWithTheRepoName()
    {
        // "LoreFetchData" starts with the literal characters "LoreFetch" --
        // a naive string.StartsWith(repoRoot) check would wrongly refuse
        // this. The guard must compare path segments, not raw prefixes.
        var cacheDir = Path.Combine("C:" + Path.DirectorySeparatorChar, "Repos", "LoreFetchData", "scryfall-cache");

        CacheDirectoryGuard.EnsureOutsideRepo(cacheDir, RepoRoot); // must not throw
    }
}
