# LoreFetch.Capture

The FlashCap → `IFrameSource` adapter: opens the webcam, negotiates a format and yields `CameraFrame`s. The Capture domain in the [domain map](../../docs/CONTRACTS.md#domain-map).

## Dependencies

References `LoreFetch.Core` (for `Abstractions`, notably `IFrameSource`, `IFrameSourceFactory`, `FrameSourceException`, `ScanSettings`) and is the **only project that references FlashCap** — no other project may depend on it. It also references OpenCvSharp4, but only for MJPEG decode (`Cv2.ImDecode`) and rotation; card detection and rectification stay in `Core/Imaging`.

Grants `InternalsVisibleTo` to `LoreFetch.Tests.Capture` for its internal capture stage.

## Concrete name fixed for integration

`LoreFetch.Capture.WebcamFrameSourceFactory : IFrameSourceFactory`, constructed with `(ILoggerFactory)` — see [`../../docs/history/orchestration-plan.md`](../../docs/history/orchestration-plan.md) §4. The App wires this in at integration; nothing outside this project may construct it before then.

## The C920 trap

USB 2.0 bandwidth, DSHOW-over-MSMF backend preference — see [`../../CLAUDE.md`](../../CLAUDE.md#the-c920-trap) for the bandwidth arithmetic. What this project actually does about it is **not** `VideoCapture`'s "set `FourCC` before width/height, then read the properties back and assert" — that's the fallback path for if FlashCap itself doesn't work out (see `docs/design/capture.md`'s *Fallbacks*). The shipped path is FlashCap's own explicit negotiation: `FlashCapDeviceCatalog.Enumerate` calls `EnumerateDescriptors()` and logs every backend's full characteristic list, then `CaptureDeviceSelector.SelectDevice` picks the one `VideoCharacteristics` matching exactly `1920x1080`, `PixelFormats.JPEG`, `>= 30 fps` — preferring DirectShow, falling back to Media Foundation, never Video for Windows — **or throws with that full enumerated list in the message** rather than silently opening a lower mode. There is no `set()`-and-hope call to get wrong in the first place.

## Testing

Unit-tested in `../../Tests/Capture`. Hardware verification (live device, sustained memory, negotiated format) is manual on the Windows PC — see [`../../docs/TESTING.md`](../../docs/TESTING.md).

## Internals

FlashCap is confined to two files: `FlashCapDeviceCatalog` (enumeration) and `WebcamFrameSourceFactory` (open, start, wire-up, `IFrameSourceFactory`). Everything else in this project — and everything a caller receives — never references the FlashCap assembly. Per-frame data flows one way, callback thread to consumer thread:

```
FlashCap capture callback (OnFrameArrived, in WebcamFrameSourceFactory)
  → JpegFrameChannel.Push        — copies FlashCap's scope-limited ArraySegment into an
                                    ArrayPool<byte>.Shared-rented buffer (never .Create() —
                                    see CLAUDE.md's C920 trap table on why that silently
                                    stops pooling anything this size), then offers it to a
                                    capacity-1, BoundedChannelFullMode.DropOldest channel
                                    whose itemDropped callback disposes (and thus returns
                                    to the pool) whatever it evicts
  → FrameWatchdog.Watch          — races each MoveNextAsync against ScanSettings'
                                    FirstFrameTimeoutMs (once) / FrameWatchdogMs (every
                                    item after), turning silence into FrameSourceException
                                    since FlashCap itself cannot detect unplug / in-use /
                                    permission-denied
  → JpegFrameDecoder.Decode      — Cv2.ImDecode(ReadOnlySpan<byte>, Color) to a BGR24 Mat,
                                    Cv2.Rotate per ScanSettings.CameraRotationDegrees (read
                                    once, at open — never per frame), copied into a pooled
                                    CameraFrame buffer
  → CameraFrame                  — what WebcamFrameSource.ReadAsync actually yields
```

Frames are dropped *before* decoding, not after — `JpegFrameChannel` carries raw JPEG bytes (~800 KB at the C920's bus-limited MJPEG rate) rather than decoded BGR24 (6.2 MB), so a slow consumer never pays for a decode it would immediately discard.

`ScanSettings.PreferredDeviceId` round-trips through `CaptureDescriptorFormatting`, the one place its format is defined: `"{Backend}:{IdentityText}"` (e.g. `"DirectShow:\\?\usb#vid_046d..."`), using `CaptureBackend`'s own enum member name. The prefix exists because Windows enumeration concatenates up to three backends for one physical camera (DirectShow, Media Foundation, and Video for Windows — new in FlashCap 1.12.0 — with no backend selector), so one C920 can enumerate as three different ids.

Backend preference for automatic selection (`CaptureDeviceSelector`): **DirectShow, then Media Foundation — Video for Windows is never selected**, even when named explicitly via `PreferredDeviceId`, because it's a legacy path that typically misreports modern modes. A **zero-descriptor enumeration is its own diagnosis** — `FrameSourceException` naming that no camera may be connected *or* camera access may be denied by a Windows privacy setting, since an empty list looks identical either way — distinct from the "devices exist but none match" diagnosis, which carries the full enumerated characteristic list so a user can see what the device offered instead.
