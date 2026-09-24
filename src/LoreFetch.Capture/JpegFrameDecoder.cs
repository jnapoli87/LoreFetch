using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Capture;

/// Decodes one MJPEG frame into a pooled BGR24 `CameraFrame`, applying the
/// source's fixed rotation on the way. This is C2a ("FlashCap does not
/// decode MJPEG... this stream must") and C3 ("apply the rotation here, so
/// everything downstream sees an already-upright frame") from
/// docs/design/capture.md. Internal — only the FlashCap-facing shim and
/// its tests ever hold one of these; the scan pipeline only ever sees the
/// `CameraFrame` that comes out the other end.
///
/// Decode happens only for frames that survive `JpegFrameChannel` — the
/// drop point sits entirely upstream of this class, so a slow consumer
/// never pays for a decode it would immediately discard.
internal sealed class JpegFrameDecoder
{
    /// Every frame's decode time is logged individually at Debug — cheap,
    /// and the only way to see an isolated slow decode while developing —
    /// and rolled up into an Information-level summary every this many
    /// frames (about 5 s at the C920's 30 fps), so a sustained run doesn't
    /// spam Information with 30 lines a second while still surfacing the
    /// number DECISIONS.md calls out as unmeasured: "30 × 1080p JPEG
    /// decodes/sec on the CPU."
    internal const int DecodeSummaryIntervalFrames = 150;

    private readonly RotateFlags? _rotateFlags;
    private readonly ArrayPool<byte> _pool;
    private readonly ILogger _logger;

    // Single-threaded bookkeeping: Decode is only ever called by the one
    // consumer draining JpegFrameChannel.ReadAsync (itself single-reader by
    // construction), so none of this needs synchronization.
    private int _framesSinceSummary;
    private double _decodeMsSinceSummary;
    private double _maxDecodeMsSinceSummary;

    /// `rotationDegrees` is read once, here, at construction — never per
    /// frame. `ScanSettings.CameraRotationDegrees` is a mutable property
    /// and `IFrameSource.Geometry` is a plain property consumers may
    /// cache, so the delivered dimensions must not change mid-stream: a
    /// rotation change takes effect on the next source, not the next frame
    /// (docs/design/capture.md C3).
    ///
    /// Throws `ArgumentOutOfRangeException` for anything other than 0, 90,
    /// 180 or 270 — `Cv2.Rotate` has exactly three non-identity
    /// `RotateFlags`, so nothing else is implementable, and validating
    /// here means a bad setting fails at open rather than on the first
    /// frame.
    internal JpegFrameDecoder(int rotationDegrees, ArrayPool<byte>? pool = null, ILogger? logger = null)
    {
        _rotateFlags = ToRotateFlags(rotationDegrees);
        _pool = pool ?? ArrayPool<byte>.Shared;
        _logger = logger ?? NullLogger.Instance;
    }

    /// The geometry a source built around this decoder's rotation setting
    /// would report, given whatever width/height the negotiated device (or,
    /// in tests, the encoded JPEG) actually decodes to. 90 and 270 swap the
    /// axes — a 1920×1080 source becomes 1080×1920 — 0 and 180 don't.
    /// `RotationDegrees` on the result records what was applied; per the
    /// contract it is informational and must never be re-applied downstream.
    internal static FrameGeometry ComputeGeometry(int decodedWidth, int decodedHeight, int rotationDegrees)
    {
        return rotationDegrees is 90 or 270
            ? new FrameGeometry(decodedHeight, decodedWidth, rotationDegrees)
            : new FrameGeometry(decodedWidth, decodedHeight, rotationDegrees);
    }

    /// Decodes and rotates one frame. Always disposes `jpegFrame` — this
    /// method takes ownership of it on entry, success or failure, so a
    /// caller never needs its own success/failure branch just to return
    /// the JPEG buffer.
    ///
    /// Returns null when the bytes don't decode as an image. FlashCap only
    /// ever hands this stage bytes from a stream already negotiated as
    /// JPEG, so a decode failure here means one corrupt frame — a USB
    /// glitch, a partial capture — not proof the device is gone; C1c's
    /// frame watchdog is what turns a *run* of these into a
    /// `FrameSourceException`. Skipping one bad frame rather than faulting
    /// the whole enumerator mirrors the detector's own "better to detect
    /// nothing than hash a hand."
    internal CameraFrame? Decode(PooledJpegFrame jpegFrame)
    {
        using (jpegFrame)
        {
            var stopwatch = Stopwatch.StartNew();

            using var decoded = Cv2.ImDecode(jpegFrame.Bytes.Span, ImreadModes.Color);
            if (decoded.Empty())
            {
                _logger.LogWarning(
                    "Capture: dropped one frame captured at {CapturedAt:O} — {Length} bytes did not decode as an image.",
                    jpegFrame.CapturedAt,
                    jpegFrame.Length);
                return null;
            }

            var frame = _rotateFlags is { } flag
                ? RotateAndCopy(decoded, flag, jpegFrame.CapturedAt)
                : CopyToPooledFrame(decoded, jpegFrame.CapturedAt);

            stopwatch.Stop();
            RecordDecodeTime(stopwatch.Elapsed.TotalMilliseconds);
            return frame;
        }
    }

    private CameraFrame RotateAndCopy(Mat decoded, RotateFlags flag, DateTimeOffset capturedAt)
    {
        // Cv2.Rotate has no in-place overload usable here (source and
        // destination must differ), so this is a second Mat purely for the
        // rotate step — small next to the 6.2 MB pooled copy that follows,
        // and disposed immediately after.
        using var rotated = new Mat();
        Cv2.Rotate(decoded, rotated, flag);
        return CopyToPooledFrame(rotated, capturedAt);
    }

    private CameraFrame CopyToPooledFrame(Mat mat, DateTimeOffset capturedAt)
    {
        var width = mat.Width;
        var height = mat.Height;
        var stride = width * 3; // PixelLayout.Bgr24, packed — our own stride choice, not the Mat's.
        var length = stride * height;
        var matStep = (int)mat.Step();

        var buffer = _pool.Rent(length);
        for (var row = 0; row < height; row++)
        {
            var srcRow = IntPtr.Add(mat.Data, row * matStep);
            Marshal.Copy(srcRow, buffer, row * stride, stride);
        }

        return new CameraFrame(buffer, width, height, stride, PixelLayout.Bgr24, capturedAt, _pool);
    }

    private void RecordDecodeTime(double elapsedMs)
    {
        _logger.LogDebug("Capture: decoded frame in {ElapsedMs:F2} ms.", elapsedMs);

        _framesSinceSummary++;
        _decodeMsSinceSummary += elapsedMs;
        _maxDecodeMsSinceSummary = Math.Max(_maxDecodeMsSinceSummary, elapsedMs);

        if (_framesSinceSummary < DecodeSummaryIntervalFrames)
        {
            return;
        }

        _logger.LogInformation(
            "Capture: decoded {FrameCount} frames, average {AverageMs:F2} ms, max {MaxMs:F2} ms.",
            _framesSinceSummary,
            _decodeMsSinceSummary / _framesSinceSummary,
            _maxDecodeMsSinceSummary);

        _framesSinceSummary = 0;
        _decodeMsSinceSummary = 0;
        _maxDecodeMsSinceSummary = 0;
    }

    private static RotateFlags? ToRotateFlags(int rotationDegrees) => rotationDegrees switch
    {
        0 => null,
        90 => RotateFlags.Rotate90Clockwise,
        180 => RotateFlags.Rotate180,
        270 => RotateFlags.Rotate90Counterclockwise,
        _ => throw new ArgumentOutOfRangeException(
            nameof(rotationDegrees),
            rotationDegrees,
            "CameraRotationDegrees must be 0, 90, 180 or 270 — Cv2.Rotate has no other case."),
    };
}
