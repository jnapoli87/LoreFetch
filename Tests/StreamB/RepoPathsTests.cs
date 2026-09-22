using LoreFetch.Lab;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// `RepoPaths.FindRepoRoot` used to walk up ONLY from the running exe's own
/// directory, so a Lab build copied out of the repo tree threw an
/// unhandled exception -- one whose hardcoded message named "--out" even
/// when the failing command was `images` (B4b-era defect, fixed here in
/// B4c). These tests drive the internal, testable overload against
/// throwaway temp directories rather than the real process paths, which a
/// unit test cannot control.
public sealed class RepoPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lorefetch-repopaths-tests", Guid.NewGuid().ToString("N"));

    public RepoPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup only
        }
    }

    [Fact]
    public void TryFindRepoRoot_FindsSolutionInFirstCandidateDirectory()
    {
        var repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "LoreFetch.slnx"), "");
        var exeDir = Path.Combine(repoDir, "src", "LoreFetch.Lab", "bin", "Release", "net10.0");
        Directory.CreateDirectory(exeDir);

        var found = RepoPaths.TryFindRepoRoot([exeDir], out var repoRoot);

        Assert.True(found);
        Assert.Equal(Path.GetFullPath(repoDir), Path.GetFullPath(repoRoot!));
    }

    [Fact]
    public void TryFindRepoRoot_FallsBackToSecondCandidate_WhenFirstHasNoSolutionAboveIt()
    {
        // Simulates "a Lab build copied out of the repo": the exe's own
        // directory (and everything above it) never contains LoreFetch.slnx,
        // but the working directory (the second candidate) sits inside a
        // real checkout.
        var outsideExeDir = Path.Combine(_root, "somewhere-else", "bin");
        Directory.CreateDirectory(outsideExeDir);

        var repoDir = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "LoreFetch.slnx"), "");
        var cwd = Path.Combine(repoDir, "some", "nested", "cwd");
        Directory.CreateDirectory(cwd);

        var found = RepoPaths.TryFindRepoRoot([outsideExeDir, cwd], out var repoRoot);

        Assert.True(found);
        Assert.Equal(Path.GetFullPath(repoDir), Path.GetFullPath(repoRoot!));
    }

    [Fact]
    public void TryFindRepoRoot_ReturnsFalse_WhenNeitherCandidateFindsTheSolution()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        var found = RepoPaths.TryFindRepoRoot([a, b], out var repoRoot);

        Assert.False(found);
        Assert.Null(repoRoot);
    }

    [Fact]
    public void FindRepoRoot_RealProcessPaths_StillWorksInsideThisCheckout()
    {
        // Not a synthetic case -- this asserts the PUBLIC entry point
        // (which uses the real AppContext.BaseDirectory / CurrentDirectory)
        // still finds the real repo when run the normal way, i.e. that the
        // fix did not break the working case while fixing the broken one.
        var repoRoot = RepoPaths.FindRepoRoot();

        Assert.True(File.Exists(Path.Combine(repoRoot, "LoreFetch.slnx")));
    }
}
