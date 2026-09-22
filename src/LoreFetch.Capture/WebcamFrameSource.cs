using System.Runtime.CompilerServices;
using FlashCap; // extension methods only (StopAsync) — the type itself is always spelled FlashCap.CaptureDevice below.
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Capture;

/// The concrete `IFrameSource` this stream builds: FlashCap's opened
/// device feeding `JpegFrameChannel` (C1a) from its capture callback,
/// `FrameWatchdog` (C1c) turning silence into `FrameSourceException`, and
/// `JpegFrameDecoder` (C1b) turning surviving JPEG bytes into rotated,
/// pooled `CameraFrame`s. `Description` and `Geometry` are fixed once, at
/// construction, from whatever `WebcamFrameSourceFactory.CreateAsync`
/// actually negotiated — never from the requested constants (this
/// stream's "Done when": "Is `Description` the negotiated format or the
/// requested one?").
///
/// Internal: nothing outside this project ever holds one of these
/// directly — callers only ever see it through the `IFrameSource`
/// returned by `WebcamFrameSourceFactory.CreateAsync`.
internal sealed class WebcamFrameSource : IFrameSource
{
    private readonly FlashCap.CaptureDevice _device;
    private readonly JpegFrameChannel _channel;
    private readonly JpegFrameDecoder _decoder;
    private readonly FrameWatchdog _watchdog;
    private readonly ILogger _logger;
    private int _disposed;

    internal WebcamFrameSource(
        FlashCap.CaptureDevice device,
        JpegFrameChannel channel,
        JpegFrameDecoder decoder,
        FrameWatchdog watchdog,
        string description,
        FrameGeometry geometry,
        ILogger logger)
    {
        _device = device;
        _channel = channel;
        _decoder = decoder;
        _watchdog = watchdog;
        _logger = logger;
        Description = description;
        Geometry = geometry;
    }

    public string Description { get; }

    public FrameGeometry Geometry { get; }

    /// Watches `_channel.ReadAsync` for silence (turning it into
    /// `FrameSourceException` per C5) and decodes every surviving JPEG
    /// into a rotated, pooled `CameraFrame` (C2a/C3). A single reader on
    /// the device falls out of composition: `_channel.ReadAsync` itself
    /// throws `InvalidOperationException` on a second concurrent
    /// enumeration, and this method never enumerates it more than once
    /// per call.
    public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var jpegFrame in _watchdog.Watch(_channel.ReadAsync(ct), ct).ConfigureAwait(false))
        {
            // Decode takes ownership of jpegFrame (returns its buffer to
            // the pool) whether it succeeds or not, so there is no
            // separate cleanup branch here — see JpegFrameDecoder.Decode.
            var frame = _decoder.Decode(jpegFrame);
            if (frame is not null)
            {
                yield return frame;
            }
        }
    }

    /// Stops and releases the FlashCap device and completes the channel.
    /// Idempotent, and safe to call whether the source is healthy, mid
    /// enumeration, or already faulted — every step is best-effort so one
    /// failing release (e.g. the device is already gone) never prevents
    /// the next one from running.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _device.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture: StopAsync threw while disposing the frame source — the device may already be gone.");
        }

        try
        {
            await _device.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture: CaptureDevice.DisposeAsync threw while disposing the frame source.");
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
