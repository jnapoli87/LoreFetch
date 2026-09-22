using System.Diagnostics;
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

    // Deliberately far apart (5 s vs 100 ms) for MidStreamStall: the two
    // timeouts must never be confusable by value alone. Pins the branch,
    // not just the outcome — a watchdog that (bug) applies the first-frame
    // timeout to every item would still eventually throw within
    // TestTimeout's 10 s bound, so "an exception eventually arrives" alone
    // doesn't prove the frame-timeout branch fired. Verified by
    // chaos-testing exactly that bug: with the previous ShortTimeout
    // (60 ms) used for both roles in that test, the wrong branch was
    // indistinguishable from the right one.
    private static readonly TimeSpan MidStreamFirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MidStreamFrameTimeout = TimeSpan.FromMilliseconds(100);

    // Generous against CI jitter, but two orders of magnitude below
    // MidStreamFirstFrameTimeout — an exception arriving under this bound
    // could not have come from the first-frame timer.
    private static readonly TimeSpan MidStreamStallDetectionBound = TimeSpan.FromSeconds(2);

    // 60 frames * FastFramePace = 600 ms — six full periods of the healthy
    // test's 100 ms frameTimeout, not just one. See that test's comment.
    private const int HealthyStreamFrameCount = 60;

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

            // Mirrors the frame-timeout assertion in MidStreamStall below:
            // the message must report the timeout value that actually
            // applied — here the first-frame budget, ShortTimeout — not
            // just some plausible-sounding number.
            Assert.Contains($"{ShortTimeout.TotalMilliseconds:F0} ms", exception.Message, StringComparison.OrdinalIgnoreCase);
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
            // firstFrameTimeout (5 s) is deliberately ~50x frameTimeout
            // (100 ms) — see MidStreamFirstFrameTimeout's comment. Without
            // that gap, "an exception eventually arrived" doesn't prove
            // *which* timer fired: a watchdog that (bug) applied the
            // first-frame timeout on every iteration instead of resetting
            // to the frame timeout after the first item would still throw
            // well inside TestTimeout's 10 s bound, and the assertions
            // below are what actually catch that.
            var watchdog = new FrameWatchdog(firstFrameTimeout: MidStreamFirstFrameTimeout, frameTimeout: MidStreamFrameTimeout);

            var seen = 0;
            var stopwatch = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<FrameSourceException>(async () =>
            {
                await foreach (var _ in watchdog.Watch(FramesThenStall(count: 3, pace: FastFramePace, stallAfter: true, ct: cts.Token), cts.Token))
                {
                    seen++;
                }
            }).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            // The stall must be detected as a mid-stream gap, not confused
            // with the first-frame case. Three independent checks pin the
            // branch, not just the outcome:
            Assert.Equal(3, seen);
            Assert.Contains("frames were flowing", exception.Message, StringComparison.OrdinalIgnoreCase);
            // The message must name the *frame* timeout's own value, not
            // just any number.
            Assert.Contains($"{MidStreamFrameTimeout.TotalMilliseconds:F0} ms", exception.Message, StringComparison.OrdinalIgnoreCase);
            // And it must have fired far sooner than the first-frame timer
            // ever could — a bound two orders of magnitude below
            // MidStreamFirstFrameTimeout, so this could not pass by
            // accident if the wrong timer were driving it.
            Assert.True(
                stopwatch.Elapsed < MidStreamStallDetectionBound,
                $"Expected the frame-timeout branch to fire within {MidStreamStallDetectionBound}, but it took {stopwatch.Elapsed} — closer to firstFrameTimeout ({MidStreamFirstFrameTimeout}) than frameTimeout ({MidStreamFrameTimeout}) suggests the wrong timer fired.");
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    [Fact]
    public async Task HealthyStream_PacedWellInsideTheWatchdog_NeverTrips()
    {
        // frameTimeout is a 10x multiple of the pace (100 ms budget per
        // 10 ms gap) — comfortable per-frame margin, but the run as a whole
        // spans HealthyStreamFrameCount * FastFramePace = 600 ms, i.e. six
        // full frameTimeout periods. That length matters as much as the
        // per-frame margin: a correct implementation resets its deadline on
        // every frame and sails through all six periods, but an
        // implementation that resets the deadline only once — say, after
        // the first frame, and never again — accumulates real elapsed time
        // against a single stale deadline, so it necessarily trips partway
        // through this run. A shorter run (previously 30 frames against a
        // 300 ms budget — one period, not several) left that regression a
        // near-miss: only 3 of 5 chaos runs failed. Several periods make it
        // fail every time.
        var frameTimeout = TimeSpan.FromMilliseconds(100);
        var watchdog = new FrameWatchdog(firstFrameTimeout: LongTimeout, frameTimeout: frameTimeout);

        var seen = 0;
        var ct = TestContext.Current.CancellationToken;

        // Bounded independently of the watchdog's own timeouts — see
        // TestTimeout's comment. A regression that spuriously trips the
        // watchdog surfaces as a FrameSourceException well before this
        // bound; a regression that instead makes it hang would otherwise
        // hang this test forever.
        await RunAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(HealthyStreamFrameCount, seen);

        async Task RunAsync()
        {
            await foreach (var _ in watchdog.Watch(FramesThenStall(count: HealthyStreamFrameCount, pace: FastFramePace, stallAfter: false, ct: ct), ct))
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
