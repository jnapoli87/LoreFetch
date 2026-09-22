using LoreFetch.Core.Abstractions;

namespace LoreFetch.Capture;

/// Picks which enumerated device and characteristic to open, and produces
/// every diagnosis this stream's contract requires when nothing usable is
/// found. Pure: takes plain `CaptureDescriptor` data and an optional
/// preferred id, returns a plain `Selection` or throws
/// `FrameSourceException` — nothing in this file references FlashCap,
/// which is what makes selection and diagnosis unit-testable without a
/// camera (docs/stream-c-capture.md "Done when": "Split the stream in
/// two... everything else... behind an internal stage").
///
/// Never silently falls back to a lower mode: every path either returns an
/// exact match for 1920x1080 JPEG at >= 30 fps, or throws with the full
/// enumerated list in the message (stream-c-capture.md C2).
internal static class CaptureDeviceSelector
{
    internal const int RequiredWidth = 1920;
    internal const int RequiredHeight = 1080;
    internal const string RequiredPixelFormat = "JPEG";
    internal const double MinimumFramesPerSecond = 30.0;

    /// Backends this stream will ever open, in preference order. Video for
    /// Windows (and anything folded into `CaptureBackend.Other`) is
    /// deliberately absent from this list — CLAUDE.md: "a legacy path that
    /// typically misreports modern modes" — so a VfW-only match falls
    /// through to the same "no matching format" diagnosis as no match at
    /// all. `SelectPreferred` enforces the same exclusion explicitly, so
    /// the rule holds even when a caller supplies `PreferredDeviceId`
    /// directly: Video for Windows is never chosen, full stop.
    private static readonly CaptureBackend[] PreferenceOrder = [CaptureBackend.DirectShow, CaptureBackend.MediaFoundation];

    /// The chosen descriptor and characteristic, plus their indices into
    /// the caller's original lists — `WebcamFrameSourceFactory` needs the
    /// indices to find the matching *real* FlashCap objects to open,
    /// without this (FlashCap-free) type ever having to carry them.
    internal readonly record struct Selection(
        int DescriptorIndex,
        int CharacteristicIndex,
        CaptureDescriptor Descriptor,
        CaptureCharacteristic Characteristic);

    internal static Selection SelectDevice(IReadOnlyList<CaptureDescriptor> descriptors, string? preferredDeviceId)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        if (descriptors.Count == 0)
        {
            // A distinct diagnosis from "no matching format": an empty
            // enumeration means either no camera is connected or camera
            // access has been denied by a Windows privacy/permission
            // setting, and FlashCap cannot tell those two apart — an
            // absent device and a denied one both enumerate to nothing
            // (stream-c-capture.md C1: "Zero descriptors is its own
            // diagnosis... not an empty list").
            throw new FrameSourceException(
                "No capture devices were enumerated at all. This means either no camera is connected, or " +
                "camera access has been denied by a Windows privacy/permission setting — an empty enumeration " +
                "looks identical either way, so this cannot be narrowed further from here.");
        }

        return preferredDeviceId is not null
            ? SelectPreferred(descriptors, preferredDeviceId)
            : SelectFirstMatchByBackendPreference(descriptors);
    }

    private static Selection SelectFirstMatchByBackendPreference(IReadOnlyList<CaptureDescriptor> descriptors)
    {
        foreach (var backend in PreferenceOrder)
        {
            for (var i = 0; i < descriptors.Count; i++)
            {
                if (descriptors[i].Backend != backend)
                {
                    continue;
                }

                if (TryMatchCharacteristic(descriptors[i], out var characteristicIndex, out var characteristic))
                {
                    return new Selection(i, characteristicIndex, descriptors[i], characteristic);
                }
            }
        }

        throw new FrameSourceException(
            $"No enumerated device offers {RequiredWidth}x{RequiredHeight} {RequiredPixelFormat} at >= " +
            $"{MinimumFramesPerSecond:0.#} fps on DirectShow or Media Foundation (Video for Windows is never " +
            "selected). Enumerated devices:" + Environment.NewLine + CaptureDescriptorFormatting.FormatList(descriptors));
    }

    private static Selection SelectPreferred(IReadOnlyList<CaptureDescriptor> descriptors, string preferredDeviceId)
    {
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];

            // Video for Windows (and anything folded into `Other`) is never
            // chosen, even when a caller names it explicitly by id — the
            // same rule `PreferenceOrder` encodes for the automatic path.
            if (descriptor.Backend is CaptureBackend.VideoForWindows or CaptureBackend.Other)
            {
                continue;
            }

            if (!string.Equals(CaptureDescriptorFormatting.FormatDeviceId(descriptor), preferredDeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryMatchCharacteristic(descriptor, out var characteristicIndex, out var characteristic))
            {
                return new Selection(i, characteristicIndex, descriptor, characteristic);
            }

            throw new FrameSourceException(
                $"Preferred device '{preferredDeviceId}' was found but offers no characteristic matching " +
                $"{RequiredWidth}x{RequiredHeight} {RequiredPixelFormat} at >= {MinimumFramesPerSecond:0.#} fps. " +
                "Its characteristics: " + CaptureDescriptorFormatting.FormatCharacteristics(descriptor.Characteristics));
        }

        throw new FrameSourceException(
            $"Preferred device '{preferredDeviceId}' (ScanSettings.PreferredDeviceId) was not found among the " +
            "enumerated devices. Available device ids:" + Environment.NewLine + CaptureDescriptorFormatting.FormatIds(descriptors));
    }

    private static bool TryMatchCharacteristic(CaptureDescriptor descriptor, out int characteristicIndex, out CaptureCharacteristic characteristic)
    {
        var characteristics = descriptor.Characteristics;
        for (var i = 0; i < characteristics.Count; i++)
        {
            var c = characteristics[i];
            if (c.Width == RequiredWidth && c.Height == RequiredHeight &&
                string.Equals(c.PixelFormat, RequiredPixelFormat, StringComparison.Ordinal) &&
                c.FramesPerSecond >= MinimumFramesPerSecond)
            {
                characteristicIndex = i;
                characteristic = c;
                return true;
            }
        }

        characteristicIndex = -1;
        characteristic = default;
        return false;
    }
}
