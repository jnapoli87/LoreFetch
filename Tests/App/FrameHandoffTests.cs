using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// Regression test for the A2 data race: the old design let the UI thread
/// convert from a REFERENCE to a buffer the pipeline's background thread
/// could still be overwriting, which rendered torn frames (top of one
/// frame, bottom of the next). `FrameHandoff` is pure — no Avalonia, no
/// camera, no UI thread — so it is tested directly: one producer thread
/// publishing many frames, one consumer thread taking them concurrently,
/// while asserting the two properties a torn/racy read would violate:
///
///   1. Every taken buffer is UNIFORM — every byte in it equal to the
///      first, because each published frame is filled entirely with one
///      sequence-derived byte. A torn read straddling two publishes would
///      produce a buffer with more than one distinct byte value in it.
///   2. `Sequence` (carried in the metadata, never through the buffer
///      bytes) never goes backwards across the frames the consumer
///      actually observes — coalescing is allowed to skip frames, but
///      never to deliver an older one after a newer one.
///
/// Bounded via a hard iteration count and a CancellationTokenSource timeout
/// rather than a fixed sleep, so this is fast (well under 2 s) and does not
/// flake green-when-broken: with tens of thousands of iterations and a
/// buffer large enough that a torn read almost certainly crosses a byte
/// boundary, a real race reproduces on effectively every run. (Verified by
/// temporarily reintroducing the old single-buffer bug and running this
/// test repeatedly — not part of this file; see the stream-a package
/// report.)
public class FrameHandoffTests
{
    private const int FrameCount = 20_000;

    // Large enough that a torn read (part of one publish, part of the
    // next) is overwhelmingly likely to straddle a byte where the two
    // frames' fill values differ, rather than by chance landing entirely
    // within one publish's already-written region.
    private const int BufferLength = 64 * 1024;

    [Fact]
    public async Task ProducerConsumer_NeverTearsABufferAndNeverGoesBackwards()
    {
        var handoff = new FrameHandoff();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var quads = Array.Empty<CardQuad>();

        var producer = Task.Run(() =>
        {
            var source = new byte[BufferLength];
            for (var seq = 1; seq <= FrameCount; seq++)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // Every byte of this frame's buffer is the same value, so
                // ANY two differing bytes in a buffer the consumer takes
                // prove a torn read.
                Array.Fill(source, unchecked((byte)seq));
                handoff.Publish(source, BufferLength, 1, BufferLength, PixelLayout.Bgr24, quads, seq);
            }
        }, cts.Token);

        long lastSequence = 0;
        var takenCount = 0;
        var spin = new SpinWait();

        var consumer = Task.Run(() =>
        {
            while (!producer.IsCompleted && !cts.IsCancellationRequested)
            {
                if (handoff.TryTake(out var buffer, out var metadata))
                {
                    CheckFrame(buffer.Span, metadata.Sequence, ref lastSequence);
                    takenCount++;
                    handoff.Return();
                    spin.Reset();
                }
                else
                {
                    // Back off instead of hammering the lock on every
                    // empty poll — this is a plain busy loop otherwise,
                    // and unthrottled contention with the producer's own
                    // lock use would just slow both threads down.
                    spin.SpinOnce();
                }
            }

            // Drain whatever is left after the producer finishes, so a
            // fast producer / slow consumer split still gets the final
            // published frame checked.
            while (handoff.TryTake(out var buffer, out var metadata))
            {
                CheckFrame(buffer.Span, metadata.Sequence, ref lastSequence);
                takenCount++;
                handoff.Return();
            }
        }, cts.Token);

        await Task.WhenAll(producer, consumer);

        Assert.False(cts.IsCancellationRequested, "Test timed out — producer or consumer made no progress.");
        Assert.True(takenCount > 0, "Consumer never took a single frame.");
        Assert.Equal(FrameCount, lastSequence);
    }

    private static void CheckFrame(ReadOnlySpan<byte> buffer, long sequence, ref long lastSequence)
    {
        var first = buffer[0];
        var badIndex = buffer.IndexOfAnyExcept(first);
        if (badIndex != -1)
        {
            Assert.Fail(
                $"Torn buffer for sequence {sequence}: byte 0 is {first} but byte {badIndex} is {buffer[badIndex]}.");
        }

        Assert.True(sequence >= lastSequence, $"Sequence went backwards: {sequence} after {lastSequence}.");
        lastSequence = sequence;
    }
}
