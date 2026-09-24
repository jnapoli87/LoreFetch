using Xunit;

namespace LoreFetch.Tests.Lab.RoundTrip;

/// Unit coverage for `RoundTripGateTests.ResolveCacheDir`'s testable
/// overload -- every branch of the resolution order documented on the
/// method itself, exercised without touching real environment variables
/// or the real filesystem. See package brief: on the Windows PC, without
/// `LOREFETCH_SCRYFALL_CACHE` set, the cache lives at
/// `C:\LoreFetchData\scryfall-cache` (CLAUDE.md), not under the user's
/// home directory, and every cache-gated test in this project was
/// skipping silently as a result.
public class ResolveCacheDirTests
{
    // Built with Path.Combine, as ResolveCacheDir builds them, because these
    // tests pin the resolution ORDER, not the separator. Hardcoded "\\"
    // literals only ever matched on Windows: on the macOS leg Path.Combine
    // joins with "/", so every branch that returns a candidate failed there.
    private const string HomeDir = "C:\\Users\\test";
    private static readonly string HomeCandidate = Path.Combine(HomeDir, "LoreFetchData", "scryfall-cache");
    private static readonly string WindowsCandidate = Path.Combine("C:" + Path.DirectorySeparatorChar, "LoreFetchData", "scryfall-cache");

    private static string Resolve(
        string? overrideValue,
        bool homeExists,
        bool windowsCandidateExists,
        bool isWindows)
    {
        return LoreFetch.Tests.Lab.RoundTrip.RoundTripGateTests.ResolveCacheDir(
            _ => overrideValue,
            path => path == HomeCandidate ? homeExists : path == WindowsCandidate && windowsCandidateExists,
            HomeDir,
            isWindows);
    }

    [Fact]
    public void EnvVarSetAndNonBlank_WinsOverEverything()
    {
        var result = Resolve(
            overrideValue: "D:\\SomewhereElse\\cache",
            homeExists: true,
            windowsCandidateExists: true,
            isWindows: true);

        Assert.Equal("D:\\SomewhereElse\\cache", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EnvVarSetButBlank_IsIgnored_FallsThroughToDirectoryProbing(string? blankValue)
    {
        var result = Resolve(
            overrideValue: blankValue,
            homeExists: true,
            windowsCandidateExists: true,
            isWindows: true);

        // Falls through to step 2 (home candidate exists) rather than
        // returning the blank/whitespace override verbatim.
        Assert.Equal(HomeCandidate, result);
    }

    [Fact]
    public void BothCandidatesExist_HomeWins()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: true,
            windowsCandidateExists: true,
            isWindows: true);

        Assert.Equal(HomeCandidate, result);
    }

    [Fact]
    public void OnlyWindowsCandidateExists_OnWindows_WindowsCandidateWins()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: false,
            windowsCandidateExists: true,
            isWindows: true);

        Assert.Equal(WindowsCandidate, result);
    }

    [Fact]
    public void OnlyWindowsCandidateExists_ButNotOnWindows_WindowsCandidateIsIgnored()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: false,
            windowsCandidateExists: true,
            isWindows: false);

        // Not Windows, so the Windows candidate is never even probed --
        // falls all the way through to the home path, which does not
        // exist either, per step 4.
        Assert.Equal(HomeCandidate, result);
    }

    [Fact]
    public void NeitherCandidateExists_OnWindows_FallsBackToHomePath()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: false,
            windowsCandidateExists: false,
            isWindows: true);

        Assert.Equal(HomeCandidate, result);
    }

    [Fact]
    public void NeitherCandidateExists_NotOnWindows_FallsBackToHomePath()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: false,
            windowsCandidateExists: false,
            isWindows: false);

        Assert.Equal(HomeCandidate, result);
    }

    [Fact]
    public void OnlyHomeCandidateExists_HomeWins()
    {
        var result = Resolve(
            overrideValue: null,
            homeExists: true,
            windowsCandidateExists: false,
            isWindows: true);

        Assert.Equal(HomeCandidate, result);
    }
}
