using LoreFetch.Capture;
using Xunit;

namespace LoreFetch.Tests.StreamC;

/// Exercises `JpegFrameChannel`, the internal newest-frame-only stage
/// between the (untestable) FlashCap shim and the (not-yet-written) decode
/// step. This is the grant `InternalsVisibleTo("LoreFetch.Tests.StreamC")`
/// in LoreFetch.Capture.csproj existed to prove — see PlaceholderTests.cs's
/// former comment, which this file replaces.
public class JpegFrameChannelTests
{
    [Fact]
    public async Task ReadAsync_ReturnsNewestPushedFrame_WhenSeveralWerePushedBeforeAnyRead()
    {
        await using var channel = new JpegFrameChannel();

        // Capacity 1 + DropOldest means only the last of these should ever
        // reach a reader; each byte value tags which push it came from.
        for (var i = 0; i < 10; i++)
        {
            channel.Push([(byte)i], DateTimeOffset.UtcNow);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var sawAny = false;
        await foreach (var frame in channel.ReadAsync(cts.Token))
        {
            sawAny = true;
            Assert.Equal(9, frame.Bytes.Span[0]);
            frame.Dispose();
            break;
        }

        Assert.True(sawAny, "expected a frame to be available without ever pushing after the loop above");
    }

    [Fact]
    public async Task DroppedFrames_AreDisposedImmediately_SoEveryRentedBufferIsEventuallyReturned()
    {
        var pool = new CountingArrayPool();
        var channel = new JpegFrameChannel(pool);

        const int pushCount = 20;
        for (var i = 0; i < pushCount; i++)
        {
            channel.Push([(byte)i], DateTimeOffset.UtcNow);
        }

        // All but the last push were evicted by DropOldest before any
        // reader ever saw them. If the channel's itemDropped callback did
        // not dispose the evicted item, this is where that leak shows up —
        // ReturnCount would sit at 0 instead of pushCount - 1.
        Assert.Equal(pushCount - 1, pool.ReturnCount);

        // The one survivor was never read either. Disposing the stage
        // itself must return it too.
        await channel.DisposeAsync();

        Assert.Equal(pushCount, pool.RentCount);
        Assert.Equal(pushCount, pool.ReturnCount);
    }

    [Fact]
    public async Task SlowConsumer_FastProducer_MemoryStaysBoundedAndEveryBufferReturns()
    {
        var pool = new CountingArrayPool();
        var channel = new JpegFrameChannel(pool);
        using var gate = new SemaphoreSlim(0, 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dequeuedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The consumer takes hold of exactly one frame and then never
        // advances past it until the test cancels — the worst case for
        // "slow consumer," and the case that most directly exercises
        // DropOldest against a fast producer.
        var consumerTask = Task.Run(
            async () =>
            {
                try
                {
                    var isFirst = true;
                    await foreach (var frame in channel.ReadAsync(cts.Token))
                    {
                        using (frame)
                        {
                            if (isFirst)
                            {
                                dequeuedFirst.SetResult();
                                isFirst = false;
                            }

                            await gate.WaitAsync(cts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected: cancellation is how this test ends the run.
                }
            },
            TestContext.Current.CancellationToken);

        // Prime the channel and wait — via a signal, not a sleep — for the
        // consumer to actually dequeue it before flooding the channel.
        // Without this handshake, "fast producer, slow consumer" could just
        // mean "the producer finished before the consumer ever ran," which
        // would prove nothing about a frame being held while more arrive.
        channel.Push([0], DateTimeOffset.UtcNow);
        await dequeuedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        const int floodCount = 500;
        for (var i = 1; i <= floodCount; i++)
        {
            channel.Push([(byte)(i % 256)], DateTimeOffset.UtcNow);
        }

        cts.Cancel();

        // Bounded rather than an open-ended await: if cancellation somehow
        // failed to unwind the consumer, this surfaces as a TimeoutException
        // (a clear test failure) instead of a hung test run.
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await channel.DisposeAsync();

        var totalPushed = floodCount + 1;
        Assert.True(
            pool.PeakOutstanding <= 5,
            $"peak outstanding rented buffers was {pool.PeakOutstanding}; expected a small constant " +
            $"regardless of {totalPushed} pushes — a channel that queues instead of dropping would let " +
            "this grow with the flood.");
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(totalPushed, pool.RentCount);
    }

    [Fact]
    public async Task ReadAsync_SecondConcurrentEnumeration_Throws()
    {
        await using var channel = new JpegFrameChannel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // An async iterator's body runs synchronously up to its first
        // suspension point, and the reader-slot guard is the very first
        // statement in JpegFrameChannel.ReadAsync — so by the time this
        // call returns, the slot is already claimed, even though the
        // channel has nothing to read yet. That makes this test
        // single-threaded and deterministic: no race to win, nothing to
        // gate.
        var firstEnumerator = channel.ReadAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMove = firstEnumerator.MoveNextAsync();

        var secondEnumerator = channel.ReadAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Bounded, not open-ended: without the reader-slot guard, a second
        // enumeration doesn't throw at all — it just blocks on the same
        // empty channel forever (confirmed by chaos-testing this guard's
        // removal). Wrapping the call in WaitAsync turns that hang into a
        // TimeoutException, so a missing guard fails the test in seconds
        // instead of stalling the run.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondEnumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        cts.Cancel();
        try
        {
            await firstMove;
        }
        catch (OperationCanceledException)
        {
            // Expected: the first enumeration was still waiting for a frame.
        }

        await firstEnumerator.DisposeAsync();
        await secondEnumerator.DisposeAsync();
    }
}
