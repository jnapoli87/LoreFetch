using FlashCap; // extension methods only (EnumerateDescriptors) — types are spelled FlashCap.X below.
using Microsoft.Extensions.Logging;

namespace LoreFetch.Capture;

/// The FlashCap-facing shim. Per docs/stream-c-capture.md's "Done when":
/// "the FlashCap-facing shim is the only code that cannot be tested...
/// keep it as close to nothing as possible." `FlashCap.CaptureDeviceDescriptor`
/// cannot be constructed as a test double — its `Identity` accessor is
/// backed by an internal implementation and the real capture entry point,
/// `InternalOpenWithFrameProcessorAsync`, is internal to FlashCap's own
/// assembly — so this class exists only to enumerate real devices, map
/// each one to the plain `CaptureDescriptor`/`CaptureCharacteristic` shape
/// the rest of this project (and every test) works with, and to hand back
/// the real FlashCap objects `WebcamFrameSourceFactory` needs to actually
/// open a device. Nothing here is worth a unit test: the mapping is a 1:1
/// field copy, and the log line is diagnostic output, not behaviour.
internal static class FlashCapDeviceCatalog
{
    /// Pairs a mapped, testable `CaptureDescriptor` with the real FlashCap
    /// descriptor and its characteristic array, indexed identically to
    /// `Mapped.Characteristics` — kept out of `CaptureDescriptor` itself so
    /// that type, and everything that reads it, stays FlashCap-free.
    internal sealed record EnumeratedDevice(
        CaptureDescriptor Mapped,
        FlashCap.CaptureDeviceDescriptor Raw,
        IReadOnlyList<FlashCap.VideoCharacteristics> RawCharacteristics);

    /// Enumerates every backend FlashCap exposes on this platform — on
    /// Windows, DirectShow, Video for Windows and Media Foundation,
    /// concatenated with no selector, so one physical camera can appear up
    /// to three times (stream-c-capture.md C1) — and logs each one's
    /// backend and full characteristic list at Information, permanently:
    /// C1 calls this "the fastest way to explain any capture problem
    /// later, on any user's machine."
    internal static IReadOnlyList<EnumeratedDevice> Enumerate(ILogger logger)
    {
        var raws = new FlashCap.CaptureDevices().EnumerateDescriptors().ToList();
        var result = new List<EnumeratedDevice>(raws.Count);

        foreach (var raw in raws)
        {
            var backend = ToBackend(raw.DeviceType);
            var identityText = raw.Identity?.ToString() ?? string.Empty;
            var rawCharacteristics = (IReadOnlyList<FlashCap.VideoCharacteristics>?)raw.Characteristics ?? [];
            var characteristics = rawCharacteristics
                .Select(c => new CaptureCharacteristic(c.Width, c.Height, c.PixelFormat.ToString(), (double)c.FramesPerSecond))
                .ToList();
            var mapped = new CaptureDescriptor(backend, identityText, raw.Name ?? string.Empty, characteristics);

            result.Add(new EnumeratedDevice(mapped, raw, rawCharacteristics));

            logger.LogInformation(
                "Capture: enumerated {Backend} device \"{Name}\" [{Identity}] with {Count} characteristic(s): {Characteristics}",
                backend,
                mapped.Name,
                identityText,
                characteristics.Count,
                CaptureDescriptorFormatting.FormatCharacteristics(characteristics));
        }

        if (result.Count == 0)
        {
            // The selector raises the FrameSourceException for this case;
            // this line is the same diagnosis surfacing in the log even
            // when nothing downstream ever catches that exception.
            logger.LogWarning("Capture: zero devices enumerated — no camera connected, or camera access denied.");
        }

        return result;
    }

    private static CaptureBackend ToBackend(FlashCap.DeviceTypes deviceType) => deviceType switch
    {
        FlashCap.DeviceTypes.DirectShow => CaptureBackend.DirectShow,
        FlashCap.DeviceTypes.MediaFoundation => CaptureBackend.MediaFoundation,
        FlashCap.DeviceTypes.VideoForWindows => CaptureBackend.VideoForWindows,
        _ => CaptureBackend.Other, // AVFoundation, V4L2 — never enumerated on win-x64, never selected anywhere.
    };
}
