using System.Threading.Channels;
using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LoreFetch.Tests.Capture;

/// Exercises `FrameWatchdog` on a `FakeTimeProvider`: frames arrive when the
/// test writes them and time moves only when the test advances it, so a
/// loaded CI runner can't stretch a gap past a timeout (the previous
/// real-timer version tripped on macOS CI). That also makes the
/// boundaries exact — a gap one tick short of a timeout must never trip,
/// and a gap of exactly the timeout must always trip.
public class FrameWatchdogTests
{
    // Far apart (5 s vs 100 ms) so the two budgets can't be confused by
    // value: a watchdog that applies the wrong one to a gap fails the
    // boundary assertions below, not just some eventual-exception check.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    // Real-time bound on every await, independent of the fake clock. Nothing
    // here should ever wait in real time, so this only matters when a
    // regression makes the watchdog hang: the test then fails instead of
    // stalling the run.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FirstFrame_ArrivingJustInsideTheFirstFrameBudget_IsDelivered()
    {
        await using var stream = new WatchedStream();

        // 50 frame timeouts pass before the first frame: only the
        // first-frame budget may apply to this wait.
        stream.Request();
        stream.Advance(FirstFrameTimeout - OneTick);

        Assert.Equal(1, await stream.Deliver(1));
    }

    [Fact]
    public async Task FirstFrameTimeout_NoFrameEver_ThrowsFrameSourceException()
    {
        await using var stream = new WatchedStream();

        stream.Request();
        stream.Advance(FirstFrameTimeout);

        var exception = await stream.ExpectTimeout();
        Assert.Contains("opening the capture device", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The message must report the budget that actually applied.
        Assert.Contains($"{FirstFrameTimeout.TotalMilliseconds:F0} ms", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MidStreamStall_ReachingTheFrameTimeout_ThrowsFrameSourceException()
    {
        await using var stream = new WatchedStream();

        for (var frame = 1; frame <= 3; frame++)
        {
            stream.Request();
            Assert.Equal(frame, await stream.Deliver(frame));
        }

        // Exactly the frame timeout, far short of the first-frame one: a
        // watchdog that (bug) kept the first-frame budget after the first
        // frame never trips here, and the await below hits TestTimeout.
        stream.Request();
        stream.Advance(FrameTimeout);

        var exception = await stream.ExpectTimeout();
        Assert.Contains("frames were flowing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{FrameTimeout.TotalMilliseconds:F0} ms", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthyStream_EachGapJustInsideTheFrameTimeout_NeverTrips()
    {
        await using var stream = new WatchedStream();

        stream.Request();
        Assert.Equal(0, await stream.Deliver(0));

        // Ten gaps of one tick under the frame timeout: ten periods in
        // total. A correct watchdog resets its deadline on every frame and
        // passes; one that resets it only once (bug) trips on the second
        // gap, every run.
        for (var frame = 1; frame <= 10; frame++)
        {
            stream.Request();
            stream.Advance(FrameTimeout - OneTick);
            Assert.Equal(frame, await stream.Deliver(frame));
        }

        stream.Request();
        await stream.ExpectEnd();
    }

    [Fact]
    public async Task ExceptionMessage_NamesAllThreeCandidateCauses()
    {
        await using var stream = new WatchedStream();

        stream.Request();
        stream.Advance(FirstFrameTimeout);

        var exception = await stream.ExpectTimeout();

        // The three causes docs/design/capture.md's C5 says FlashCap
        // cannot tell apart: unplugged, in use by another application,
        // and a denied permission/privacy setting.
        Assert.Contains("unplugged", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already in use by another application", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// A `FrameWatchdog` over a channel the test writes, on a clock the test
    /// moves. Each wait goes Request → (Advance) → Deliver or ExpectTimeout.
    ///
    /// The ordering is what makes this deterministic. `Request` calls the
    /// watchdog's `MoveNextAsync`, which runs synchronously up to its
    /// `Task.WhenAny` race, so the timer for that wait is already registered
    /// with the fake clock when `Request` returns. An `Advance` after it
    /// therefore always counts against that wait; one made before it would
    /// not count at all, because a `FakeTimeProvider` timer starts from the
    /// time it is created.
    private sealed class WatchedStream : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Channel<int> _frames = Channel.CreateUnbounded<int>();
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly IAsyncEnumerator<int> _watched;
        private Task<bool>? _pending;

        public WatchedStream()
        {
            var watchdog = new FrameWatchdog(FirstFrameTimeout, FrameTimeout, _time);
            _watched = watchdog.Watch(_frames.Reader.ReadAllAsync(_cts.Token), _cts.Token).GetAsyncEnumerator(_cts.Token);
        }

        public void Request() => _pending = _watched.MoveNextAsync().AsTask();

        public void Advance(TimeSpan by) => _time.Advance(by);

        /// Writes `frame` and returns what the watchdog yielded. If the
        /// watchdog already tripped, its `FrameSourceException` surfaces
        /// here: once the timer fires, a later frame can't win the race.
        public async Task<int> Deliver(int frame)
        {
            _frames.Writer.TryWrite(frame);
            Assert.True(await Pending());
            return _watched.Current;
        }

        public Task<FrameSourceException> ExpectTimeout() =>
            Assert.ThrowsAsync<FrameSourceException>(Pending);

        public async Task ExpectEnd()
        {
            _frames.Writer.Complete();
            Assert.False(await Pending());
        }

        private Task<bool> Pending() =>
            (_pending ?? throw new InvalidOperationException("Call Request() first."))
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            // Cancel first: after a timeout, the watchdog is still waiting
            // on the channel off to the side, and only cancellation ends it.
            await _cts.CancelAsync();
            await _watched.DisposeAsync();
            _cts.Dispose();
        }
    }
}
