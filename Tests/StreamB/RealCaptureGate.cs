using Xunit;

namespace LoreFetch.Tests.StreamB;

/// The same `LOREFETCH_REQUIRE_REAL` switch as `Tests/Integration`'s
/// `RealArtifactGate` (orchestration finding V7) -- reimplemented here,
/// rather than referenced, because `Tests/Integration` is frozen contract
/// surface (CLAUDE.md) and this stream cannot add a project reference to
/// it. Unset -- the CI default -- a missing real-capture fixture
/// (`test-images/ad-hoc/`, which can never be committed: CLAUDE.md "Never
/// commit card imagery") is an honest skip that states its reason. Set to
/// `"1"`, the SAME condition is a FAILURE instead, so the machine that
/// actually has the fixtures cannot quietly pass with the real-capture
/// check untested.
public static class RealCaptureGate
{
    public const string RequireRealEnvVar = "LOREFETCH_REQUIRE_REAL";

    /// Skips with `reason` -- unless `LOREFETCH_REQUIRE_REAL=1`, in which
    /// case the same call fails with `reason` instead.
    public static void SkipOrFail(string reason)
    {
        if (Environment.GetEnvironmentVariable(RequireRealEnvVar) == "1")
        {
            Assert.Fail($"[{RequireRealEnvVar}=1] {reason}");
        }
        else
        {
            Assert.Skip(reason);
        }
    }
}
