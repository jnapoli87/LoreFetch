using System.Buffers;

namespace LoreFetch.Capture;

/// One undecoded JPEG frame, held in a rented buffer, exactly as the capture
/// callback delivers it (docs/design/capture.md C2a: "FlashCap does not decode
/// MJPEG"). Internal — nothing outside this project ever sees a JPEG frame
/// directly; the scan pipeline only ever consumes the decoded, rotated
/// `CameraFrame` that `IFrameSource.ReadAsync` yields once C1b's decode step
/// exists.
///
/// Pooling *here*, one step before decode, is the reason this stage drops
/// before decoding rather than after: a dropped frame then costs only the
/// ~800 KB JPEG copy, not a decode plus a 6.2 MB BGR24 buffer (C4, "Drop
/// before decoding, not after").
internal sealed class PooledJpegFrame : IDisposable
{
    // Interlocked.Exchange mirrors CameraFrame's own guard in
    // Core/Abstractions/Frames.cs: whichever caller wins the exchange
    // returns the real buffer, every other caller is a silent no-op. A plain
    // bool guard would race and could return the same rented array twice —
    // the exact bug pooling exists to prevent.
    private byte[]? _buffer;
    private readonly ArrayPool<byte> _pool;

    internal PooledJpegFrame(byte[] buffer, int length, DateTimeOffset capturedAt, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(pool);

        _buffer = buffer;
        Length = length;
        CapturedAt = capturedAt;
        _pool = pool;
    }

    /// The number of valid bytes at the front of the rented buffer — the
    /// buffer itself is usually longer, because `ArrayPool` rounds up.
    internal int Length { get; }

    internal DateTimeOffset CapturedAt { get; }

    /// Exactly `Length` bytes. Throws once the frame has been disposed:
    /// unlike `CameraFrame.Pixels`, which is captured once at construction
    /// over a buffer only its owner returns, this buffer can be reclaimed by
    /// the channel's own `itemDropped` callback while a stale reference is
    /// still held elsewhere, so a read after dispose must not silently hand
    /// back memory another owner may already be reusing.
    internal ReadOnlyMemory<byte> Bytes
    {
        get
        {
            var buffer = _buffer;
            ObjectDisposedException.ThrowIf(buffer is null, this);
            return buffer.AsMemory(0, Length);
        }
    }

    /// Idempotent; a second call is a silent no-op, not a throw and not a
    /// second return to the pool.
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer);
        }
    }
}
