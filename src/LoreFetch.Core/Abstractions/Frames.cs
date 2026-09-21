using System.Buffers;

namespace LoreFetch.Core.Abstractions;

public enum PixelLayout
{
    Bgr24,
    Bgra32,
}

/// One frame. Owns a pooled buffer; Dispose returns it to the pool.
/// Not thread-safe: one owner at a time, and the owner disposes.
public sealed class CameraFrame : IDisposable
{
    // Interlocked.Exchange makes the pool-return exactly-once even if
    // Dispose is somehow re-entered or called from two places: whichever
    // call wins the exchange sees the real buffer, every other call sees
    // null and is a silent no-op. A plain bool guard would race under that
    // same scenario, and a double-returned pooled array means two owners
    // then share one buffer — the worst class of bug this type exists to
    // prevent.
    private byte[]? _buffer;
    private readonly ArrayPool<byte>? _pool;

    /// `buffer` may be longer than Stride*Height (pooled arrays round up).
    /// When `pool` is non-null, Dispose returns `buffer` to it exactly once.
    public CameraFrame(
        byte[] buffer,
        int width,
        int height,
        int stride,
        PixelLayout layout,
        DateTimeOffset capturedAt,
        ArrayPool<byte>? pool)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Width = width;
        Height = height;
        Stride = stride;
        Layout = layout;
        CapturedAt = capturedAt;
        // Exactly Stride*Height bytes, even though the pooled buffer is
        // usually longer (ArrayPool rounds up) — slice it here, once, so
        // every reader of Pixels sees only real frame data.
        Pixels = buffer.AsMemory(0, stride * height);

        _buffer = buffer;
        _pool = pool;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; } // may exceed Width*bpp
    public PixelLayout Layout { get; }
    public DateTimeOffset CapturedAt { get; }
    public ReadOnlyMemory<byte> Pixels { get; } // exactly Stride*Height bytes

    /// Idempotent; a second call is a silent no-op, not a throw and not a
    /// second return.
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool?.Return(buffer);
        }
    }
}

/// Geometry of the frame **as delivered downstream**, i.e. post-rotation.
/// `Width`/`Height` always equal the `CameraFrame.Width`/`Height` a consumer
/// sees, so a quad in frame coordinates maps directly onto them.
/// `RotationDegrees` records the rotation the source ALREADY APPLIED — it is
/// informational. Never re-apply it; doing so transposes every quad.
public readonly record struct FrameGeometry(int Width, int Height, int RotationDegrees);

/// Produces frames: a camera, or a folder of images.
public interface IFrameSource : IAsyncDisposable
{
    /// Human-readable, for logs and the UI status line — what was actually
    /// negotiated, not what was requested.
    /// e.g. "Logitech C920 1920x1080 MJPG @30fps" or "folder: fixtures/10in"
    string Description { get; }

    FrameGeometry Geometry { get; }

    /// Newest-frame-only semantics: implementations MUST drop the STALE frame
    /// and keep the newest, so a slow consumer sees latency, not a backlog.
    /// A dropped frame MUST still be disposed — a Channel with capacity 1 and
    /// DropOldest does NOT dispose the item it evicts, so use the
    /// `itemDropped` overload or the pooled buffer leaks on every drop.
    /// Throws FrameSourceException when the device fails or disappears.
    IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct);
}

/// A device failure: unplugged, in use by another application, permission
/// denied, or no frame within the configured timeout. Distinguishable from
/// cancellation (OperationCanceledException) and from ordinary end-of-stream,
/// so callers never have to match on message text.
public sealed class FrameSourceException : Exception
{
    public FrameSourceException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// Opens a frame source. Construction is async because the negotiated format
/// is only known after the device is opened — which is what `Description` and
/// `Geometry` must report. Without this, both properties would be wrong until
/// the first frame arrives, and a "no such device" failure would surface from
/// inside the enumerator instead of at startup.
public interface IFrameSourceFactory
{
    /// Throws FrameSourceException, listing the enumerated devices, when the
    /// requested device is absent or no usable format can be negotiated.
    Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct);
}
