using System.Runtime.CompilerServices;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Capture.Hardware;

/// C4-fix's unit-level proof. `HardwareCameraTests` and `UnplugCameraTests`
/// open a real device and are `[Trait("Category","Hardware")]` — never run
/// by CI or by this implementer's own "run the suite" step — so the fix to
/// their shared harness helper, `HardwareTestSupport.MoveNextWithHarnessBoundAsync`,
/// gets no coverage from either. This class exercises that helper directly
/// against a synthetic `IAsyncEnumerable&lt;int&gt;` instead of a camera, so
/// it runs unattended in CI and proves the fix without hardware. Deliberately
/// NOT traited `Category=Hardware`.
///
/// Both synthetic sources below are genuine async-iterator methods (not a
/// hand-written `IAsyncEnumerator&lt;T&gt;`), specifically so that disposing
/// one while its `MoveNextAsync` is still pending reproduces the exact
/// `NotSupportedException` the compiler-generated iterator throws in that
/// case — the same behaviour `FrameWatchdog.Watch`'s own comment documents
/// (src/LoreFetch.Capture/FrameWatchdog.cs) and the one this fix exists to
/// stop the harness from tripping over.
///
/// Chaos-tested per docs/TESTING.md — see the C4-fix commit message and the
/// task report for the two mutations applied and reverted, and the failure
/// each produced.
public sealed class HardwareTestSupportMoveNextBoundTests
{
    /// Cause 1 in the C4-fix defect: a per-MoveNext bound shorter than the
    /// product's own delay masks the product's exception behind a harness
    /// timeout. Proven here in the *fixed* direction — margin is generous
    /// enough (500 ms) relative to the source's 100 ms delay that the real
    /// `FrameSourceException` reaches the caller untouched.
    [Fact]
    public async Task SurfacesProductException_WhenSourceThrowsBeforeHarnessBoundExpires()
    {
        var settings = new ScanSettings { FirstFrameTimeoutMs = 50, FrameWatchdogMs = 50 };
        using var cts = new CancellationTokenSource();
        await using var enumerator = ThrowsAfterDelay(TimeSpan.FromMilliseconds(100), cts.Token).GetAsyncEnumerator(cts.Token);

        var ex = await Assert.ThrowsAsync<FrameSourceException>(
            () => HardwareTestSupport.MoveNextWithHarnessBoundAsync(
                enumerator,
                settings,
                cts,
                TestContext.Current.CancellationToken,
                margin: TimeSpan.FromMilliseconds(500)));

        Assert.Equal("synthetic device failure", ex.Message);
    }

    /// Cause 2 in the C4-fix defect: a harness-side timeout must never
    /// dispose an enumerator while its `MoveNextAsync` is still pending.
    /// The source here never yields (stands in for "the device stopped
    /// delivering frames and the product's own watchdog is broken"), so the
    /// helper's own bound is guaranteed to expire — proving it cancels,
    /// drains, and only then throws `HarnessTimeoutException`, never
    /// `NotSupportedException`, and that the enumerator is safe to dispose
    /// immediately afterward.
    [Fact]
    public async Task FailsWithHarnessTimeoutMessage_NeverNotSupportedException_WhenSourceHangs()
    {
        var settings = new ScanSettings { FirstFrameTimeoutMs = 50, FrameWatchdogMs = 50 };
        using var cts = new CancellationTokenSource();
        var enumerator = NeverYields(cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            var ex = await Assert.ThrowsAsync<HarnessTimeoutException>(
                () => HardwareTestSupport.MoveNextWithHarnessBoundAsync(
                    enumerator,
                    settings,
                    cts,
                    TestContext.Current.CancellationToken,
                    margin: TimeSpan.FromMilliseconds(100),
                    settleTimeout: TimeSpan.FromSeconds(1)));

            Assert.Contains("harness", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FirstFrameTimeoutMs=50", ex.Message, StringComparison.Ordinal);
            Assert.Contains("FrameWatchdogMs=50", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            // The point of this test: by the time
            // MoveNextWithHarnessBoundAsync returned (by throwing), the
            // MoveNextAsync it started is guaranteed to have settled
            // (cancelled), so this DisposeAsync — unlike the pre-fix
            // harness's — never races a pending MoveNextAsync and never
            // throws NotSupportedException.
            await enumerator.DisposeAsync();
        }
    }

    /// Never produces an item and never completes on its own — only `ct`
    /// (cancelled by the helper under test) ends it. Stands in for "the
    /// device stopped delivering frames."
    private static async IAsyncEnumerable<int> NeverYields([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        yield break; // unreachable; satisfies the compiler's flow analysis for an async-iterator method.
    }

    /// Waits `delay`, then throws — standing in for the product's own
    /// `FrameWatchdog` eventually raising `FrameSourceException`. Delays
    /// via `ct` like a real capture source would (so that a too-short
    /// harness bound cancelling `ct` cuts this off with
    /// `OperationCanceledException`, exactly reproducing "cause 1" of the
    /// C4-fix defect when chaos-tested with a shortened bound).
    private static async IAsyncEnumerable<int> ThrowsAfterDelay(TimeSpan delay, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        throw new FrameSourceException("synthetic device failure");
#pragma warning disable CS0162 // Unreachable code detected — required so the compiler treats this as an async-iterator method.
        yield break;
#pragma warning restore CS0162
    }
}
