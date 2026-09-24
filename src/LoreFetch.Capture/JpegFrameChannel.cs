using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LoreFetch.Capture;

/// Newest-frame-only handoff from the capture callback thread to whatever
/// decodes JPEGs into `CameraFrame`s (C1b). This is the piece
/// docs/design/capture.md's C4 identifies as the part FlashCap's own queue
/// cannot provide: `QueuingProcessor.OnFrameArrived` drops the *newest*
/// arrival while an older queued frame waits — the exact inverse of what a
/// slow consumer needs — so newest-frame-only semantics are entirely this
/// channel's job, not FlashCap's.
///
/// Carries raw JPEG bytes, not decoded pixels, precisely so a dropped frame
/// is cheap: see the class comment on `PooledJpegFrame`.
///
/// Single-reader by construction (`SingleReader = true`) is not enough on
/// its own — `ReadAsync` still returns an `IAsyncEnumerable`, and nothing
/// stops a caller enumerating it twice. A second concurrent enumeration
/// throws `InvalidOperationException` instead of silently letting two loops
/// race the same channel.
internal sealed class JpegFrameChannel : IAsyncDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly Channel<PooledJpegFrame> _channel;

    // 0 = no active reader, 1 = one enumeration in flight. CompareExchange
    // makes "claim the single reader slot" atomic; a plain read-then-set
    // would let two concurrent MoveNextAsync calls both observe "unclaimed"
    // and both proceed.
    private int _hasActiveReader;
    private int _disposed;

    /// `pool` defaults to `ArrayPool&lt;byte&gt;.Shared` — never
    /// `ArrayPool&lt;byte&gt;.Create()`, whose configurable pool throws away
    /// anything above its `DefaultMaxArrayLength` rather than pooling it (see
    /// DECISIONS.md's C920 trap table: every frame buffer here is well past
    /// that limit). A caller may inject a wrapping pool to observe rents and
    /// returns, e.g. in tests — the same pattern `FolderFrameSource.Open`
    /// already uses.
    internal JpegFrameChannel(ArrayPool<byte>? pool = null)
    {
        _pool = pool ?? ArrayPool<byte>.Shared;

        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        };

        // BoundedChannelFullMode.DropOldest is documented as "removes and
        // ignores" the evicted item — it never disposes it. Without this
        // itemDropped callback, every frame the fast producer outruns the
        // consumer on leaks its rented buffer, which is precisely the
        // slow-consumer load this channel exists to survive.
        _channel = Channel.CreateBounded<PooledJpegFrame>(options, static frame => frame.Dispose());
    }

    /// Copies `jpeg` into a rented buffer and offers it to the channel,
    /// evicting (and disposing) whatever frame is currently queued. Called
    /// from the capture callback thread; copying immediately is what lets
    /// the FlashCap-facing shim (C2) hand back FlashCap's own
    /// scope-limited `ArraySegment` right away instead of retaining it.
    internal void Push(ReadOnlySpan<byte> jpeg, DateTimeOffset capturedAt)
    {
        var buffer = _pool.Rent(jpeg.Length);
        jpeg.CopyTo(buffer);
        var frame = new PooledJpegFrame(buffer, jpeg.Length, capturedAt, _pool);

        // Capacity 1 + DropOldest makes TryWrite succeed unconditionally
        // while the channel is open — full doesn't mean rejected, it means
        // "evict and take this one instead." The only way TryWrite can fail
        // is a push racing a completed (disposed) channel; in that case
        // nothing will ever read `frame`, so return its buffer immediately
        // rather than leaking it silently.
        if (!_channel.Writer.TryWrite(frame))
        {
            frame.Dispose();
        }
    }

    /// Newest-frame-only enumeration. Throws `InvalidOperationException` on
    /// a second concurrent enumeration.
    internal async IAsyncEnumerable<PooledJpegFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _hasActiveReader, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "JpegFrameChannel already has an active enumeration; only one reader is permitted at a time.");
        }

        try
        {
            await foreach (var frame in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            Volatile.Write(ref _hasActiveReader, 0);
        }
    }

    /// Completes the channel and disposes any frame still sitting in it —
    /// the one a reader never got to, e.g. because `ReadAsync` was never
    /// called, or stopped before draining. Without this, that buffer would
    /// never come back to the pool.
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var leftover))
        {
            leftover.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
