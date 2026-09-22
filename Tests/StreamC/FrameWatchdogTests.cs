using System.Runtime.CompilerServices;
using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamC;

/// Exercises `FrameWatchdog` against small, real, bounded timeouts rather
/// than a fake clock — see the class comment on `FrameWatchdog` for why.
/// Every test here has a hard outer bound (`TestContext.Current
/// .CancellationToken` plus, where a source must "never" yield, an
/// explicit long-but-finite cap) so a regression fails fast instead of
/// hanging the run.
public class FrameWatchdogTests
{
    // Generous multiples of each other so scheduler jitter on a loaded CI
    // box can't turn a correct implementation into a flaky failure.
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastFramePace = TimeSpan.FromMilliseconds(10);

    // Every awaited operation below is also bounded by this, independent of
    // whatever timeout FrameWatchdog itself is configured with. Relying
    // solely on the watchdog's own timeout to end a test means a regression
    // that breaks the timeout mechanism (e.g. never arming the first-frame
    // timer) hangs the test indefinitely instead of failing it — verified
    // by chaos-testing exactly that bug. Comfortably above every timeout
    // and pace used below (max is LongTimeout at 5 s) without being so
    // large that a real hang stalls the suite.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FirstFrameTimeout_NoFrameEver_ThrowsFrameSourceException()
    {
        // A linked, locally-owned token: NeverYields() awaits it forever
        // once orphaned (nobody awaits its underlying MoveNextAsync once
        // the watchdog has already thrown), and cancelling here at
        // teardown — rather than relying solely on the test-run-wide
        // TestContext token — unblocks that background await promptly
        // instead of leaving it parked until the whole run ends.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var watchdog = new FrameWatchdog(firstFrameTimeout: ShortTimeout, frameTimeout: LongTimeout);

            var exception = await Assert.ThrowsAsync<FrameSourceException>(async () =>
            {
                await foreach (var _ in watchdog.Watch(NeverYields(cts.Token), cts.Token))
                {
                }
            }).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Contains("opening the capture device", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    [Fact]
    public async Task MidStreamStall_LongerThanFrameWatchdog_ThrowsFrameSourceException()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var watchdog = new FrameWatchdog(firstFrameTimeout: LongTimeout, frameTimeout: ShortTimeout);

            var seen = 0;
            var exception = await Assert.ThrowsAsync<FrameSourceException>(async () =>
            {
                await foreach (var _ in watchdog.Watch(FramesThenStall(count: 3, pace: FastFramePace, stallAfter: true, ct: cts.Token), cts.Token))
                {
                    seen++;
                }
            }).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // The stall must be detected as a mid-stream gap, not confused
            // with the first-frame case — the message (and this test)
            // distinguish the two so a wrong branch can't hide behind a
            // shared exception type.
            Assert.Equal(3, seen);
            Assert.Contains("frames were flowing", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    [Fact]
    public async Task HealthyStream_PacedWellInsideTheWatchdog_NeverTrips()
    {
        // frameTimeout is a large multiple of the pace, so this is not a
        // race — a correct implementation resets its deadline on every
        // frame, and 30 frames at 10 ms apart (300 ms total) never comes
        // close to a 300 ms-per-gap budget.
        var watchdog = new FrameWatchdog(firstFrameTimeout: LongTimeout, frameTimeout: TimeSpan.FromMilliseconds(300));

        var seen = 0;
        var ct = TestContext.Current.CancellationToken;

        // Bounded independently of the watchdog's own timeouts — see
        // TestTimeout's comment. A regression that spuriously trips the
        // watchdog surfaces as a FrameSourceException well before this
        // bound; a regression that instead makes it hang would otherwise
        // hang this test forever.
        await RunAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(30, seen);

        async Task RunAsync()
        {
            await foreach (var _ in watchdog.Watch(FramesThenStall(count: 30, pace: FastFramePace, stallAfter: false, ct: ct), ct))
            {
                seen++;
            }
        }
    }

    [Fact]
    public async Task ExceptionMessage_NamesAllThreeCandidateCauses()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var watchdog = new FrameWatchdog(firstFrameTimeout: ShortTimeout, frameTimeout: LongTimeout);

            var exception = await Assert.ThrowsAsync<FrameSourceException>(async () =>
            {
                await foreach (var _ in watchdog.Watch(NeverYields(cts.Token), cts.Token))
                {
                }
            }).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            // The three causes stream-c-capture.md's C5 says FlashCap
            // cannot tell apart: unplugged, in use by another application,
            // and a denied permission/privacy setting.
            Assert.Contains("unplugged", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("already in use by another application", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("permission", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    /// Never produces an item and never completes on its own — only `ct`
    /// ends it. Stands in for "the device never delivers a first frame."
    private static async IAsyncEnumerable<int> NeverYields([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break; // unreachable; satisfies the compiler's flow analysis.
    }

    /// Yields `count` items spaced `pace` apart, then — unless
    /// `stallAfter` is false — stalls forever (until `ct` fires). Stands in
    /// for "frames were flowing, then the device vanished," or, with
    /// `stallAfter: false`, for a plain healthy stream.
    private static async IAsyncEnumerable<int> FramesThenStall(
        int count,
        TimeSpan pace,
        bool stallAfter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(pace, ct);
            yield return i;
        }

        if (stallAfter)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
