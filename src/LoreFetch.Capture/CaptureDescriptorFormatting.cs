namespace LoreFetch.Capture;

/// The one place `ScanSettings.PreferredDeviceId`'s on-disk/in-memory
/// format is defined, per this stream's brief: "define the format ... in
/// one place, document it in a doc comment, and match on it." Every
/// producer (enumeration, for round-tripping a user's saved preference)
/// and every consumer (selection's match, and every diagnostic message
/// that lists devices) goes through this class, so the format exists
/// exactly once.
///
/// Format: <c>"{Backend}:{IdentityText}"</c>, e.g. <c>"DirectShow:\\?\usb#vid_046d..."</c>,
/// using `CaptureBackend`'s own enum member name (PascalCase) rather than
/// the lowercase illustration in CONTRACTS.md/CLAUDE.md — those are
/// illustrative, not a literal spec, and the enum's own name round-trips
/// losslessly via ordinal string comparison with no case-folding decision
/// to get wrong. The prefix matters on its own: Windows enumeration
/// concatenates up to three backends for one physical camera, so an
/// unprefixed id would silently stop matching if the backend preference
/// order ever changed.
internal static class CaptureDescriptorFormatting
{
    internal static string FormatDeviceId(CaptureDescriptor descriptor) => $"{descriptor.Backend}:{descriptor.IdentityText}";

    /// One id per line, indented — used in the "preferred id not found"
    /// diagnosis so a user can see exactly what IS available.
    internal static string FormatIds(IReadOnlyList<CaptureDescriptor> descriptors) =>
        string.Join(Environment.NewLine, descriptors.Select(d => "  " + FormatDeviceId(d)));

    /// One descriptor per line — id, name, and its full characteristic
    /// list — used in the "no matching format" diagnosis. This is the
    /// "enumerated list" docs/design/capture.md's C2 requires every such
    /// exception to carry, so a user can see not just that nothing matched
    /// but exactly what the device offered instead.
    internal static string FormatList(IReadOnlyList<CaptureDescriptor> descriptors) =>
        string.Join(Environment.NewLine, descriptors.Select(FormatOne));

    internal static string FormatCharacteristics(IReadOnlyList<CaptureCharacteristic> characteristics) =>
        characteristics.Count == 0
            ? "(no characteristics)"
            : string.Join(", ", characteristics.Select(c => $"{c.Width}x{c.Height} {c.PixelFormat} @{c.FramesPerSecond:0.##}fps"));

    private static string FormatOne(CaptureDescriptor descriptor) =>
        $"  {FormatDeviceId(descriptor)} \"{descriptor.Name}\": {FormatCharacteristics(descriptor.Characteristics)}";
}
