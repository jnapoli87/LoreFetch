namespace LoreFetch.Capture;

/// The backend that produced a device descriptor — mirrors FlashCap's own
/// `DeviceTypes`, kept as a separate type so every file that reads a
/// `CaptureDescriptor` (selection, diagnosis, id formatting, and their
/// tests) has no reference to the FlashCap assembly at all
/// (docs/stream-c-capture.md "Done when": "the FlashCap-facing shim is the
/// only code that cannot be tested").
///
/// `Other` folds in AVFoundation and V4L2, which this stream never
/// enumerates on Windows and never selects on any platform — like
/// `VideoForWindows`, it exists only so a descriptor from either backend
/// can still be logged and listed in a diagnosis, never so it can be
/// chosen.
internal enum CaptureBackend
{
    DirectShow,
    MediaFoundation,
    VideoForWindows,
    Other,
}

/// One negotiable characteristic within a device descriptor. `PixelFormat`
/// carries FlashCap's `PixelFormats` enum member name as plain text (e.g.
/// "JPEG", "YUYV") rather than the FlashCap enum itself, which is what
/// keeps this type — and the selection logic that reads it — free of any
/// FlashCap reference.
///
/// `FramesPerSecond` is already resolved to `double` at the point this
/// record is built. FlashCap's own `Fraction` has an implicit `double`
/// conversion, applied once during enumeration
/// (docs/stream-c-capture.md C2: compare `(double)c.FramesPerSecond >=
/// 30.0`, never `== 30` — that does not even compile against a `Fraction`,
/// and would be brittle against a `30000/1001`-style advertisement like
/// 29.97 fps).
internal readonly record struct CaptureCharacteristic(int Width, int Height, string PixelFormat, double FramesPerSecond);

/// One enumerated device, backend-tagged, with its full characteristic
/// list — the shape both C1's "log the full characteristic list" and C2's
/// selection operate over. `IdentityText` is FlashCap's own
/// `Identity.ToString()`, captured once during enumeration so nothing past
/// the enumeration step ever touches FlashCap's `object Identity` again.
internal sealed record CaptureDescriptor(
    CaptureBackend Backend,
    string IdentityText,
    string Name,
    IReadOnlyList<CaptureCharacteristic> Characteristics);
