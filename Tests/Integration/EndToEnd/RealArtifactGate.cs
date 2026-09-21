using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// The `LOREFETCH_REQUIRE_REAL` switch (orchestration finding V7). Unset —
/// the CI default — a missing real-implementation artifact (the hash index,
/// a live camera, the Scryfall cache, the fixture corpus) is an honest skip
/// that states its reason, because card imagery can never be committed and
/// the real-implementation cases can therefore never run in CI. Set to
/// `"1"`, the SAME condition is a FAILURE instead, which is what makes
/// "integration is done" mechanical on the one machine that actually has
/// every artifact (docs/TESTING.md §"Real implementations reuse the same
/// tests, gated by skip" — "Integration is done when
/// `LOREFETCH_REQUIRE_REAL=1 dotnet test` reports zero skips ... with every
/// artifact present").
///
/// One helper, called from every Real-set gate (today, just
/// `RealImplementationSet.EnsureAvailable`; every future artifact-gated
/// check funnels through it too), so the switch's behaviour lives in
/// exactly one place instead of being re-implemented — or forgotten — per
/// test case.
public static class RealArtifactGate
{
    public const string RequireRealEnvVar = "LOREFETCH_REQUIRE_REAL";

    /// Skips with `reason` — unless `LOREFETCH_REQUIRE_REAL=1`, in which
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
