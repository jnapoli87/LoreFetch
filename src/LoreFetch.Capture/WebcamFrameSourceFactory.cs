using FlashCap; // extension methods only (OpenAsync, ReferImage, ReleaseNow) — types are spelled FlashCap.X below.
using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Capture;

/// FlashCap → `IFrameSource`. The concrete type stream A's composition
/// root constructs at integration
/// (docs/orchestration-plan.md §4: fixed name
/// `LoreFetch.Capture.WebcamFrameSourceFactory`, constructed with
/// `(ILoggerFactory)`).
///
/// `CreateAsync` enumerates every backend and logs the full characteristic
/// list (C1), selects 1920x1080 JPEG at &gt;= 30 fps preferring DirectShow
/// over Media Foundation and never Video for Windows (C2), opens the
/// selected device with `maxQueuingFrames: 1` and `isScattering: false`
/// (C4), and returns an `IFrameSource` whose `Description` and `Geometry`
/// report what was actually negotiated. It throws `FrameSourceException`,
/// naming the enumerated devices, whenever nothing usable is found —
/// construction is async specifically so this failure surfaces at startup
/// rather than from inside the enumerator (see the doc comment on
/// `IFrameSourceFactory`).
public sealed class WebcamFrameSourceFactory : IFrameSourceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public WebcamFrameSourceFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebcamFrameSourceFactory>();
    }

    public async Task<IFrameSource> CreateAsync(ScanSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var devices = FlashCapDeviceCatalog.Enumerate(_logger);
        var mapped = devices.Select(d => d.Mapped).ToList();

        // Pure, FlashCap-free selection/diagnosis — see CaptureDeviceSelector.
        // Throws FrameSourceException with the enumerated list on any failure
        // to match; never silently falls back to a lower mode.
        var selection = CaptureDeviceSelector.SelectDevice(mapped, settings.PreferredDeviceId);
        var enumerated = devices[selection.DescriptorIndex];
        var rawCharacteristic = enumerated.RawCharacteristics[selection.CharacteristicIndex];

        var channel = new JpegFrameChannel();

        void OnFrameArrived(PixelBufferScope bufferScope)
        {
            // CapturedAt is taken once, here, on handler entry — not from
            // PixelBuffer.Timestamp, which is a TimeSpan on the device's
            // own clock rather than a wall-clock DateTimeOffset
            // (stream-c-capture.md C6).
            var capturedAt = DateTimeOffset.UtcNow;
            try
            {
                // ReferImage() is a zero-copy ArraySegment valid only for
                // the duration of this callback (stream-c-capture.md
                // C2a) — Push copies it into a pooled buffer immediately,
                // so nothing here retains FlashCap's own buffer.
                // ArraySegment<byte> converts implicitly to ReadOnlySpan<byte>.
                channel.Push(bufferScope.Buffer.ReferImage(), capturedAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture: failed to handle one arrived frame; skipping it.");
            }
            finally
            {
                // Releases FlashCap's own pooled buffer for this frame
                // back to its internal pool immediately, rather than
                // waiting on this synchronous delegate's own return to be
                // noticed — near-zero work in the handler, per C4.
                bufferScope.ReleaseNow();
            }
        }

        var device = await enumerated.Raw.OpenAsync(
            rawCharacteristic,
            TranscodeFormats.DoNotTranscode,
            isScattering: false,
            maxQueuingFrames: 1,
            pixelBufferArrived: OnFrameArrived,
            ct).ConfigureAwait(false);

        var geometry = JpegFrameDecoder.ComputeGeometry(selection.Characteristic.Width, selection.Characteristic.Height, settings.CameraRotationDegrees);
        var description = BuildDescription(enumerated.Mapped, selection.Characteristic);
        var decoder = new JpegFrameDecoder(settings.CameraRotationDegrees, logger: _loggerFactory.CreateLogger<JpegFrameDecoder>());
        var watchdog = new FrameWatchdog(
            TimeSpan.FromMilliseconds(settings.FirstFrameTimeoutMs),
            TimeSpan.FromMilliseconds(settings.FrameWatchdogMs));

        _logger.LogInformation("Capture: negotiated {Description}.", description);

        return new WebcamFrameSource(device, channel, decoder, watchdog, description, geometry, _loggerFactory.CreateLogger<WebcamFrameSource>());
    }

    /// Built from the negotiated characteristic — never from the requested
    /// constants — so the UI status line reflects reality rather than
    /// intent (stream-c-capture.md C2, and this stream's own "Done when":
    /// "Is `Description` the negotiated format or the requested one?").
    /// `internal` (not `private`) so it is directly unit-testable without
    /// opening a device: pass any `CaptureDescriptor`/`CaptureCharacteristic`
    /// pair and assert the negotiated numbers appear, not 1920/1080/30.
    internal static string BuildDescription(CaptureDescriptor descriptor, CaptureCharacteristic characteristic)
    {
        var fps = characteristic.FramesPerSecond % 1 == 0
            ? ((int)characteristic.FramesPerSecond).ToString()
            : characteristic.FramesPerSecond.ToString("0.##");
        return $"{descriptor.Name} {characteristic.Width}x{characteristic.Height} MJPG @{fps}fps ({descriptor.Backend})";
    }
}
