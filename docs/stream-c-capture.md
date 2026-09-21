# Stream C — Camera capture

**Smallest stream, ~100–150 lines, and the only one that needs hardware.** Its whole job is to turn a C920 into `CameraFrame`s that satisfy `IFrameSource`.

Owns (exclusive write access): `LoreFetch.Capture/**`
Consumes: `Core/Abstractions` (frozen — see [`CONTRACTS.md`](CONTRACTS.md))
Must not touch: `LoreFetch.App`, `LoreFetch.Core`, any `.csproj`, `LoreFetch.slnx`

Library: **FlashCap 1.12.0** (Apache-2.0, actively maintained). Chosen over OpenCvSharp's `VideoCapture` for one specific reason — see below.

---

## Why FlashCap rather than `VideoCapture`

The C920 is **USB 2.0**. At 1920×1080 the uncompressed YUYV mode can only advertise **5 fps** — that's bus bandwidth, not slow software. MJPG at 1080p advertises 30.

OpenCV's `set()` for FOURCC **returns `false` and silently leaves you on YUY2.** That is the origin of every "my C920 is 5 fps" report on the internet. You request MJPG, you don't get it, nothing tells you.

FlashCap's `EnumerateDescriptors()` exposes the device's real characteristic list, so you **select** 1920×1080 MJPEG from what the device actually offers rather than requesting it and hoping. That turns a silent misconfiguration into an explicit one.

If OpenCvSharp's `VideoCapture` is ever used here instead: prefer `VideoCaptureAPIs.DSHOW` (1.44 s to first frame vs MSMF's 5.71 s at 1080p — OpenCV #27917, still open, no official fix), set `FourCC = "MJPG"` **before** width/height, and **read the properties back and assert**. If MSMF is unavoidable, set `OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS=0` *before the first OpenCvSharp type is touched*, since first touch loads the native library.

---

## Tasks, in order

### C1 — Enumerate and log
`new CaptureDevices().EnumerateDescriptors()`, and **print the full characteristic list** for the C920 at startup. This is diagnostic output worth keeping permanently — it's the fastest way to explain any capture problem later, on any user's machine.

### C2 — Select the format explicitly, and fail loudly
Pick the descriptor matching `1920 × 1080`, `PixelFormats.JPEG`, 30 fps. If no such characteristic exists, **throw with the enumerated list in the message.** Do not silently fall back to a lower mode — a silent fallback to 5 fps YUY2 is precisely the failure this stream exists to prevent.

Populate `IFrameSource.Description` with what was *actually negotiated*, e.g. `"Logitech C920 1920x1080 MJPG @30fps"`, so the UI status line reflects reality rather than intent.

### C3 — Apply rotation in the source
The camera is mounted so the **1920 axis runs along the table's depth**, which means frames arrive rotated. Apply the 90° rotation **here**, so everything downstream — detector, rectifier, UI — receives an already-upright frame and nobody has to remember. Read the angle from `ScanSettings.CameraRotationDegrees`; don't hardcode 90.

### C4 — Background capture with newest-frame-only
- Capture on a background thread. Never on the UI thread.
- Hand frames off through a `Channel` with **capacity 1 and `BoundedChannelFullMode.DropOldest`**. The contract requires newest-frame-only semantics: a slow consumer must see *latency*, never a backlog. Assume the capture path buffers rather than drops unless you make it drop.
- **One reader on the device.** Never two concurrent reads on the same capture handle.
- Allocate `CameraFrame` buffers from `ArrayPool<byte>` and return them on `Dispose`. At 6.2 MB/frame × 30 fps, unpooled allocation is 186 MB/s of garbage — pooling is the point, not an optimisation.

### C5 — Device loss and lifecycle
Unplugged camera, device already in use by another app, permission denied. Surface these as a clean failure through the source rather than an unhandled exception, and make `IAsyncDisposable` genuinely release the device — a leaked handle means the next run can't open the camera, which looks like a hardware fault.

### C6 — Measure the JPEG decode cost
The C920 does MJPEG on-camera, so the host pays **zero encode** — but the app then pays **30 × 1080p JPEG decodes per second** on the CPU. This is unmeasured and is a plausible source of "it says 30 fps but feels laggy." Log per-frame decode time and report the number; if it's material, a lower preview resolution is the lever.

---

## Done when

- Pulled on the Windows PC, `dotnet build` succeeds, and live frames arrive.
- **The log shows negotiated 1920×1080 MJPG at 30 fps** — asserted from the device's own characteristics, not assumed.
- A sustained run (several minutes) shows **flat memory** — proof that pooling and disposal are correct.
- A deliberately slow consumer produces latency, not unbounded memory growth — proof that `DropOldest` is actually dropping.
- Requesting an unavailable format throws with the enumerated list in the message.
- Unplugging the camera mid-run produces a clean error, and re-plugging allows a fresh start.
- Per-frame JPEG decode time is logged.

## Fallbacks

- **FlashCap can't negotiate MJPG on this C920:** fall back to OpenCvSharp `VideoCapture` with `DSHOW` and the FOURCC-then-resolution ordering above, still asserting the read-back values. Keep the FlashCap enumeration either way — it's the diagnostic.
- **30 fps isn't achievable:** 15 fps is entirely adequate. Capture is not the bottleneck for a space-to-capture workflow; the detector only needs enough frames to evaluate settle.
- **Rotation is expensive at 1080p:** rotate only the frames handed to the detector, and let the preview render rotated via a UI transform instead.

---

## What a reviewer should scrutinise here

1. **Does the format selection actually assert?** The entire justification for this stream's library choice is avoiding a silent YUY2 fallback. If it logs a warning and continues, the bug it was meant to prevent is still present.
2. **Does newest-frame-only genuinely drop?** A `Channel` without `DropOldest`, or with capacity > 1, queues instead — which looks fine until the consumer stalls and latency grows unboundedly. Verify the channel options, not the intent.
3. **Buffer pooling and disposal.** Is every `CameraFrame` returned to the pool on exactly one `Dispose`? Double-dispose corrupts the pool; missed dispose leaks at 186 MB/s.
4. **Is rotation applied here rather than pushed downstream?** If any consumer has to know the frame is rotated, the abstraction has leaked and the bug will surface as mirrored or transposed detection.
5. **Single reader on the device** — is there any path where two reads can overlap?
6. **Is `Description` the negotiated format or the requested one?** Reporting intent rather than reality makes the status line actively misleading during debugging.
7. **Does `IAsyncDisposable` fully release the device**, including when the capture loop is mid-frame or faulted?
8. **Is `CameraRotationDegrees` read from settings**, or is 90 hardcoded?

## Risks owned by this stream

1. **Silent YUY2 fallback → 5 fps.** The headline risk, and the reason for every design choice above.
2. **Frame buffer lifetime across the thread boundary.** Pooled buffers make ownership explicit, but a frame disposed while the UI is still reading it produces corruption that only appears under load. The contract says one owner at a time — verify the handoff honours it.
3. **JPEG decode cost is unmeasured.** Could quietly consume a core.
4. **This is the only stream that cannot be verified on the Mac**, so it leans on the Windows PC and the `windows-latest` CI leg. Keep it small and keep the interface boundary clean — that's the mitigation.
