# Stream C — Camera capture

**Smallest stream, and the only one that needs hardware.** Its whole job is to turn a C920 into `CameraFrame`s that satisfy `IFrameSource`.

⚠ The original **~100–150 lines** estimate was written before this review found that FlashCap hands over undecoded JPEG and reports device loss only by falling silent. With a decode stage (C2a), a first-frame timeout and a frame watchdog (C5), it is realistically **250–350 lines** plus tests. Still the smallest stream — but not a single-sitting one.

Owns (exclusive write access): `LoreFetch.Capture/**`, `Tests/StreamC/**`, the stream C section of `README.md`
Consumes: `Core/Abstractions` (frozen — see [`CONTRACTS.md`](CONTRACTS.md)), including `ScanSettings` and the public `CameraFrame` constructor that takes a pooled buffer
Must not touch: `LoreFetch.App`, anything else in `LoreFetch.Core`, any `.csproj`, `LoreFetch.slnx`

Its only consumer is the scan pipeline, which reads exactly one `IFrameSource`.

Library: **FlashCap 1.12.0** (Apache-2.0, actively maintained; 1.12.0 is confirmed the latest published version). Chosen over OpenCvSharp's `VideoCapture` for one specific reason — see below.

Reference the **`FlashCap`** package, not `FlashCap.Core`: `OpenAsync`, `ReferImage`, `CopyImage` and `ExtractImage` are extension methods declared in the `FlashCap` assembly, while the types they extend live in `FlashCap.Core`.

---

## Why FlashCap rather than `VideoCapture`

The C920 is **USB 2.0**. At 1920×1080 the uncompressed YUYV mode can only advertise **5 fps** — that's bus bandwidth, not slow software. MJPG at 1080p advertises 30.

Verified down to the descriptor, because it is the premise of the whole stream. The C920's uncompressed 1080p frame descriptor carries `bFrameIntervalType 1` with a single interval of 0.2 s, so 5 fps is not one option among several — it is the **only** uncompressed 1080p mode the camera declares, and no negotiation can move it. The ceiling that forces this is 3072 B/microframe × 8000 = 24,576,000 B/s, i.e. **5.93 fps** for a 4,147,200-byte frame; the C920's largest isochronous setting is actually 3×1020 = 3060 B/microframe, tightening it to 5.90 fps. The next rate up its own ladder is 7.5 fps, which would need 127% of the bus. So the precise claim is *"the bus ceiling is 5.9 fps, so 5 is the highest standard rate the camera can offer"* — the doc's "capped at 5 fps" is right in effect.

The C920 also advertises **H.264** at 1080p30 on-camera, which would cut bus load further — but FlashCap's `PixelFormats` has no H.264 member, so it cannot be selected through this library. JPEG is the right and only choice here.

OpenCV's `set()` for FOURCC **returns `false` and silently leaves you on YUY2.** That is the origin of every "my C920 is 5 fps" report on the internet. You request MJPG, you don't get it, nothing tells you.

FlashCap's `EnumerateDescriptors()` exposes the device's real characteristic list, so you **select** 1920×1080 MJPEG from what the device actually offers rather than requesting it and hoping. That turns a silent misconfiguration into an explicit one.

⚠ **The enumeration is authoritative but not exhaustive.** FlashCap issue #52 records a Logitech Stream Cam whose 60 fps mode the Windows Camera app showed and FlashCap's DirectShow enumeration did not. So "not in the list" means "this backend didn't report it", not "the device can't do it" — which is why C2 throws with the list attached and why the `VideoCapture` fallback stays.

If OpenCvSharp's `VideoCapture` is ever used here instead: prefer `VideoCaptureAPIs.DSHOW` (1.44 s to first frame vs MSMF's 5.71 s at 1080p — OpenCV #27917, **now closed**, though the closure records no fix and the report also measures 2.86 s for MSMF at its default resolution, i.e. the penalty comes from `set()`-ing the resolution), set `FourCC = "MJPG"` **before** width/height, and **read the properties back and assert**. If MSMF is unavoidable, set `OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS=0` *before the first OpenCvSharp type is touched*, since first touch loads the native library.

---

## Tasks, in order

### C1 — Enumerate and log
`new CaptureDevices().EnumerateDescriptors()`, and **log the full characteristic list** for the C920 at startup. This is diagnostic output worth keeping permanently — it's the fastest way to explain any capture problem later, on any user's machine.

**Expect the same camera more than once.** On Windows `EnumerateDescriptors()` concatenates DirectShow, Video for Windows **and** Media Foundation (new in 1.12.0) with no backend selector, so one C920 yields up to three descriptors with different `Identity` values and different characteristic lists. Log the backend alongside each descriptor, or the list is unreadable. Choosing between them is an open question below.

**Zero descriptors is its own diagnosis**, not an empty list: no camera, or camera permission denied (Windows privacy setting, macOS TCC). Say which it might be rather than falling through to C2's "no matching format".

### C2 — Select the format explicitly, and fail loudly
Pick the **`VideoCharacteristics`** from `descriptor.Characteristics` (a `VideoCharacteristics[]`) matching `1920 × 1080`, `PixelFormats.JPEG`, 30 fps — you select a characteristic *within* a descriptor, not a descriptor. If no such characteristic exists, **throw with the enumerated list in the message.** Do not silently fall back to a lower mode — a silent fallback to 5 fps YUY2 is precisely the failure this stream exists to prevent.

Two details the obvious code gets wrong:

- **`FramesPerSecond` is a `Fraction`** (`readonly int Numerator` / `Denominator`), not an `int`. It has an implicit conversion to `double`, so compare `(double)c.FramesPerSecond >= 30.0` — not `== 30`, which won't compile against an int and would be brittle against a `30000/1001` style advertisement.
- **`PixelFormats.JPEG`** is the correct member and spelling; the enum is `Unknown, RGB8, RGB15, RGB16, RGB24, RGB32, ARGB32, JPEG, PNG, UYVY, YUYV, NV12`. There is no separate `MJPEG` member — MJPEG frames are JPEG frames.

**`ScanSettings.PreferredDeviceId` is a `string?`, but `CaptureDeviceDescriptor.Identity` is declared `public abstract object Identity { get; }`.** Match on `Identity.ToString()` and document that as the format, otherwise the setting can't round-trip. It is also backend-specific, so a saved id silently stops matching if the chosen backend changes.

Populate `IFrameSource.Description` with what was *actually negotiated*, e.g. `"Logitech C920 1920x1080 MJPG @30fps"`, so the UI status line reflects reality rather than intent.

### C2a — Decode the JPEG (this stream's job, and it was missing)
**FlashCap does not decode MJPEG.** Its README is explicit — *"'MJPEG' is completely the same as JPEG, so FlashCap returns the image data as is"* — and its transcoder only converts YUV/NV12 to RGB. `PixelFormats.Bgr24`/`Bgra32` do not exist on the FlashCap side; `CameraFrame.Layout` does. So selecting JPEG in C2 makes decoding **this stream's responsibility**, and the doc had no task for it.

Decode with **`Cv2.ImDecode(ReadOnlySpan<byte>, ImreadModes.Color)`**, which yields a BGR24 `Mat`; copy it into the pooled `CameraFrame` buffer and `Dispose` the `Mat` (every `ImDecode` overload allocates a new one — none writes into a caller-supplied destination). OpenCvSharp is the right choice because it is already pinned for `Core`, is native rather than managed, and the same dependency answers C3's rotation with `Cv2.Rotate`. **`LoreFetch.Capture.csproj` must therefore carry the OpenCvSharp references before the fork** — see *Proposed contract changes*.

`ReferImage()` returns an `ArraySegment<byte>` whose validity is scoped to the callback, so nothing may retain it; copy before yielding.

### C3 — Apply rotation in the source
The camera is mounted so the **1920 axis runs along the table's depth**, which means frames arrive rotated. Apply the 90° rotation **here**, so everything downstream — detector, rectifier, UI — receives an already-upright frame and nobody has to remember. Read the angle from `ScanSettings.CameraRotationDegrees`; don't hardcode 90.

- **Only 0, 90, 180 and 270 are supported** — `Cv2.Rotate` takes a `RotateFlags`, which has exactly the three non-identity cases. Validate at open and throw on anything else; `CameraRotationDegrees` is a plain `int` and nothing upstream constrains it.
- **Read it once, at open.** `ScanSettings` is a mutable class, and `IFrameSource.Geometry` is a plain property that consumers may cache — so the delivered dimensions must not change mid-stream. A rotation change takes effect on the next source, not the next frame.
- **`Geometry` reports the frame as delivered**, i.e. post-rotation: 1080 × 1920, stride 3240 for BGR24. `FrameGeometry.RotationDegrees` is then a record of what was *already applied*, not an instruction — a consumer that re-applies it is the mirrored/transposed-detection bug. See the contract clarification proposed below.

### C4 — Background capture with newest-frame-only
- Capture on a background thread. Never on the UI thread.
- Hand frames off through a `Channel` with **capacity 1 and `BoundedChannelFullMode.DropOldest`**. The contract requires newest-frame-only semantics: a slow consumer must see *latency*, never a backlog. Do not assume the capture path already gives you this — it drops, but it drops the wrong end (below).
- **One reader on the device.** Never two concurrent reads on the same capture handle. `ReadAsync` returns an `IAsyncEnumerable`, so nothing stops a caller enumerating it twice; throw `InvalidOperationException` on a second live enumerator rather than trusting the contract's "exactly one consumer".
- Allocate `CameraFrame` buffers from `ArrayPool<byte>` and return them on `Dispose`. At 6.2 MB/frame × 30 fps, unpooled allocation is 186 MB/s of garbage — pooling is the point, not an optimisation.
- Specifically **`ArrayPool<byte>.Shared`, and specifically not `ArrayPool<byte>.Create()`.** The shared pool buckets arrays up to roughly 1 GiB, so a 6.2 MB frame is genuinely pooled; `Create()` returns a `ConfigurableArrayPool` whose `DefaultMaxArrayLength` is `1024 * 1024`, and anything larger is — in the runtime's own comment — *"an array of exactly the requested length. When it's returned to the pool, we'll simply throw it away."* Every frame buffer is 6× that limit, so the tidier-looking private pool is the one that silently restores the 186 MB/s.
- Return the **rented** array, not a resized copy: `SharedArrayPool.Return` throws `ArgumentException` for an array whose length isn't a bucket size. This is why the contract says the buffer may be longer than `Stride * Height`.

Three corrections, each of which silently defeats the above:

**FlashCap's own queue drops the _newest_ frame, not the oldest.** `QueuingProcessor` holds one worker thread and begins `OnFrameArrived` with `if (this.queue.Count >= this.maxQueuingFrames) { return; }` — arriving frames are discarded while the queued *older* one waits. That is `DropNewest`, the exact inverse of what this stream needs, so **FlashCap cannot provide newest-frame-only and the channel is not a formality.** Open with `maxQueuingFrames: 1` and `isScattering: false` (scattering runs handlers in parallel and destroys frame ordering), keep the handler's work near zero, and let *our* channel define the semantics.

**A `DropOldest` channel leaks the pooled buffer of every frame it drops.** `BoundedChannelFullMode.DropOldest` is documented as "Removes and **ignores** the oldest item" — it never disposes it, so the pooled array is never returned and the pool starves back into 186 MB/s of allocation under exactly the slow-consumer load the *done-when* test applies. Construct it with the callback overload, `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T>? itemDropped)` (available since .NET 6), and pass `itemDropped: frame => frame.Dispose()`. This is the single most likely way the "flat memory" criterion fails.

**Drop before decoding, not after.** Put the JPEG bytes through the channel and decode on the consumer side, so a dropped frame costs neither a decode nor a 6.2 MB buffer. The bus ceiling caps a 30 fps MJPEG frame at ~800 KB (24,576,000 B/s ÷ 30) against 6.2 MB decoded, so the copy that buys this is roughly an eighth of the one it replaces.

### C5 — Device loss and lifecycle
Unplugged camera, device already in use by another app, permission denied. Surface these as a clean failure through the source rather than an unhandled exception, and make `IAsyncDisposable` genuinely release the device — a leaked handle means the next run can't open the camera, which looks like a hardware fault.

⚠ **These do not arrive as exceptions, so there is nothing to catch.** FlashCap has no way to detect a device already in use — issue #15 is open, labelled *help wanted* and *suspended*, and the only approach in the thread is the requester's own timer: start the device, and conclude failure if no pixel buffer has arrived within ~1 s. Unplug, in-use and permission-denied therefore all present identically, as **frames that never come**. A `catch` around `OpenAsync` cannot see any of them.

So the clean failure has to be manufactured:

- **First-frame timeout at open** — no pixel buffer within a few seconds means the device did not really start. Throw, and name the three candidate causes, because the library cannot tell them apart.
- **Frame watchdog while running** — no frame for N seconds faults the enumerator, which is what turns a mid-run unplug into an error instead of a silent freeze.
- Both thresholds belong in settings rather than as constants — see *Proposed contract changes*.

Without these, *"unplugging the camera mid-run produces a clean error"* is untestable, because the observable behaviour is a hang.

### C6 — Measure the JPEG decode cost
The C920 does MJPEG on-camera, so the host pays **zero encode** — but the app then pays **30 × 1080p JPEG decodes per second** on the CPU. This is unmeasured and is a plausible source of "it says 30 fps but feels laggy." Log per-frame decode time and report the number.

**The lever is decoding fewer frames, not a lower resolution.** Capture resolution is fixed by the geometry budget — 1080p is what yields 346 × 483 px per card at the 9.75″ mount — so dropping it would cut the identification stream's input resolution to buy preview smoothness. Decode count, by contrast, is free to cut: drop-before-decode in C4 already means only consumed frames are decoded, and the preview throttle is the UI's own concern.

`PixelBuffer.Timestamp` is a `TimeSpan` on the device's own clock, so it measures capture-to-decode latency better than wall time does; `CameraFrame.CapturedAt` still needs a `DateTimeOffset`, taken once on handler entry.

---

## Done when

- Pulled on the Windows PC, `dotnet build` succeeds, and live frames arrive.
- **The log shows negotiated 1920×1080 MJPG at 30 fps** — asserted from the device's own characteristics, not assumed.
- A sustained run (several minutes) shows **flat memory** — proof that pooling and disposal are correct.
- A deliberately slow consumer produces latency, not unbounded memory growth — proof that `DropOldest` is actually dropping.
- Requesting an unavailable format throws with the enumerated list in the message.
- Unplugging the camera mid-run produces a clean error, and re-plugging allows a fresh start.
- Per-frame JPEG decode time is logged.

**Most of that is testable without a camera, if the seam is drawn in the right place.** Split the stream in two: a thin FlashCap-facing shim that turns callbacks into JPEG byte arrays, and everything else — pooling, the `DropOldest` channel and its `itemDropped` disposal, decode, rotation, the watchdogs, `Description` and `Geometry` — behind an internal stage fed by `byte[]` JPEGs. That stage takes a synthetic JPEG on the Mac, which makes the drop, flat-memory, disposal, rotation and timeout criteria unit-testable and leaves only "live frames arrive" needing hardware.

Draw it that way deliberately, because **FlashCap cannot be faked from outside its own assembly**: `CaptureDeviceDescriptor.Identity` is abstract but the capture entry point, `InternalOpenWithFrameProcessorAsync`, is `internal`, so a test-double descriptor can be constructed and never opened. The shim is the untestable part; keep it as close to nothing as possible.

## Fallbacks

- **FlashCap can't negotiate MJPG on this C920:** try FlashCap's *other* Windows backend first — enumeration is per-backend, and issue #52 shows one backend under-reporting modes another exposes, so the Media Foundation descriptor may carry a characteristic the DirectShow one omits. That rung is free. Only then fall back to OpenCvSharp `VideoCapture` with `DSHOW` and the FOURCC-then-resolution ordering above, still asserting the read-back values. Keep the FlashCap enumeration either way — it's the diagnostic.
- **30 fps isn't achievable:** 15 fps is entirely adequate. Capture is not the bottleneck for a space-to-capture workflow; the detector only needs enough frames to evaluate settle.
- **Rotation is expensive at 1080p:** ⚠ this fallback is not available as written — it breaks the contract rather than degrading within it. "Let the preview render rotated via a UI transform" means a consumer knows the frame is rotated, which is precisely the leak *Rotation lives in the source* forbids; and the preview takes frames from the pipeline's `FrameProcessed`, not from the source, so the knowledge would have to spread through `Core/Scanning` too. Taking this rung is a contract change, not a local decision. It should also not be needed: `Cv2.Rotate` is a native transpose-and-flip, not managed per-pixel code. Measure before treating rotation as a cost at all.

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
4. **Only the FlashCap shim cannot be verified on the Mac** — corrected down from "this whole stream". With the split under *Done when*, everything except "a real device delivers frames" is exercisable off-hardware, and the Windows PC plus the `windows-latest` CI leg carry only that remainder.
5. **macOS capture must stay compile-only in CI.** FlashCap gained an AVFoundation backend in 1.11.0, so `LoreFetch.Capture` both builds *and* nominally runs on macOS — but issue #182 is open and reports a **native crash** on start/refresh plus a BGRA-vs-RGB channel mismatch, from callback state outliving its managed owner. A native crash takes down the test host rather than failing a test, so the `macos-latest` leg must never open a device. It stays what `CLAUDE.md` intends it to be: a portability check that the code compiles.

---

## Plan review: research targets

For the pre-build stream review (see [`stream-review-directions.md`](stream-review-directions.md)). Check each against primary sources — FlashCap's repository and docs, Logitech's specs — and record what you found.

1. **FlashCap 1.12.0 API.** Do `CaptureDevices().EnumerateDescriptors()`, `PixelFormats.JPEG` and the characteristic list exist as this doc describes? What does FlashCap hand the callback for an MJPEG stream — raw JPEG bytes, or decoded pixels?
2. **Who decodes MJPEG to BGR, with what?** If FlashCap hands over JPEG, this stream must decode — with which library, and is it already among the packages Stream 0 will pin? Nothing can be added after the fork.
3. **FlashCap on macOS.** `CLAUDE.md` says its macOS backend doesn't work. Does `LoreFetch.Capture` still *build* on the `macos-latest` CI leg? If not, CI needs a plan before Stream 0 writes it.
4. **The C920 claims** — USB 2.0, YUYV capped at 5 fps at 1080p, MJPG at 30 — against Logitech's published characteristics.
5. **Rotation cost.** `CameraRotationDegrees` defaults to 90, applied in the source. Is a 90° rotate of a 1080p frame at 30 fps cheap enough in managed code, or does it need a native call?
6. **The seam.** Can every task here be done with only `IFrameSource`, `CameraFrame` and `ScanSettings`? In particular, how does a device-loss error reach the user through `ReadAsync`?

---

## Plan review findings — 2026-09-21

### Verified

- FlashCap **1.12.0** is the latest published version — https://api.nuget.org/v3-flatcontainer/flashcap/index.json
- `new CaptureDevices().EnumerateDescriptors()` exists and is used exactly as this doc describes — https://github.com/kekyo/FlashCap/blob/main/README.md
- `PixelFormats.JPEG` exists with that spelling; the full enum is `Unknown, RGB8, RGB15, RGB16, RGB24, RGB32, ARGB32, JPEG, PNG, UYVY, YUYV, NV12` — https://github.com/kekyo/FlashCap/blob/main/FlashCap.Core/VideoCharacteristics.cs
- `descriptor.Characteristics` is a `VideoCharacteristics[]`, and `VideoCharacteristics` exposes `Width`, `Height`, `PixelFormat`, `FramesPerSecond`, `IsDiscrete` — same file
- FlashCap hands the callback **raw JPEG bytes** for an MJPEG stream: *"'MJPEG' is completely the same as JPEG, so FlashCap returns the image data as is"* — https://github.com/kekyo/FlashCap/blob/main/README.md
- `ReferImage()` → `ArraySegment<byte>` (no copy, scope-limited), `CopyImage()`/`ExtractImage()` → `byte[]` — https://github.com/kekyo/FlashCap/blob/main/FlashCap/PixelBufferExtension.cs
- `OpenAsync` has an overload taking `TranscodeFormats transcodeFormat, bool isScattering, int maxQueuingFrames` — https://github.com/kekyo/FlashCap/blob/main/FlashCap/CaptureDeviceDescriptorExtension.cs
- `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T>? itemDropped)` exists, from .NET 6 onward — https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.channel.createbounded
- `BoundedChannelFullMode.DropOldest` = *"Removes and ignores the oldest item in the channel"* — https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.boundedchannelfullmode
- `ArrayPool<byte>.Shared` buckets arrays to ~1 GiB (`NumBuckets = 27`, commented `SelectBucketIndex(1024 * 1024 * 1024 + 1)`), so 6.2 MB frames are genuinely pooled — https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs
- `Cv2.ImDecode(ReadOnlySpan<byte>, ImreadModes)` exists, and every `ImDecode` overload returns a new `Mat` rather than filling a caller-supplied one — https://github.com/shimat/opencvsharp/blob/main/src/OpenCvSharp/Cv2/Cv2_imgcodecs.cs
- `Cv2.Rotate(InputArray, OutputArray, RotateFlags)` exists — https://github.com/shimat/opencvsharp/blob/main/src/OpenCvSharp/Cv2/Cv2_core.cs
- `OpenCvSharp4` `4.13.0.20260627` exists and is the highest published version; no `4.12.x` exists — https://api.nuget.org/v3-flatcontainer/opencvsharp4/index.json. `OpenCvSharp4.runtime.osx.arm64` still has **exactly one** release, the same stamp — https://api.nuget.org/v3-flatcontainer/opencvsharp4.runtime.osx.arm64/index.json
- **The C920 is USB 2.0**, per Logitech's own spec table (`USB Protocol: USB 2.0`, `Frame Rate (max): 1080p@30fps`) — https://support.logi.com/hc/en-us/articles/360023307294-C920-Technical-Specifications — and per the device descriptor itself, `bcdUSB 2.00` for `046d:082d` — https://github.com/libuvc/libuvc/blob/master/cameras/logitech_hd_pro_920.txt. Logitech's separate "C920 and USB 3.0 compatibility" page is about host-port compatibility, not device bus speed, and is the usual source of confusion here.
- **YUYV at 1080p really is 5 fps and nothing else, and MJPG at 1080p really is 30.** The uncompressed 1080p frame descriptor has `bFrameIntervalType 1` with a lone `dwFrameInterval 2000000` (0.2 s), against `bFrameIntervalType 7` for MJPEG with 30 fps at the top — so this is the camera's own declaration, not a driver artifact. Corroborated by `v4l2-ctl --list-formats-ext` showing one interval for YUYV 1920×1080 and seven for MJPG — https://github.com/libuvc/libuvc/blob/master/cameras/logitech_hd_pro_920.txt and https://github.com/petminion/petminion/blob/main/docs/hardware.md
- **The 5 fps claim is arithmetically exact, not folklore.** USB 2.0 §5.6.4 and §5.9 put a high-speed endpoint at up to **3072 bytes per microframe**, and a microframe is 125 µs → 8000/s, so the isochronous ceiling is 24,576,000 B/s — https://www.usb.org/document-library/usb-20-specification. A 1920×1080 YUYV frame is 4,147,200 B (matching the descriptor's own `dwMaxVideoFrameBufferSize 4147200`), so 5 fps = 20,736,000 B/s fits and 6 fps = 24,883,200 B/s does not; the exact ceiling is 5.93 fps, or 5.90 using the C920's actual 3×1020 = 3060 B/microframe setting. The descriptor's `dwMaxBitRate 165888000` bits/s for the uncompressed mode is precisely 4,147,200 × 5.
- **MJPEG is encoded on-camera, so the host pays no encode** — the device declares `bDescriptorSubtype 6 (FORMAT_MJPEG)`, and the USB-IF MJPEG payload spec defines that as the device transmitting JPEG-compressed frames — https://www.usb.org/document-library/video-class-v15-document-set. The host-side decode cost, by contrast, has **no primary-source figure**, which independently confirms C6's premise that it is unmeasured.
- `1920×1080×3 = 6,220,800` B/frame and ×30 = 186.6 MB/s — the doc's "6.2 MB" and "186 MB/s" are right in decimal MB
- OpenCV #27917's timings are quoted correctly: 1.44 s DSHOW vs 5.71 s MSMF at 1920×1080, plus 2.86 s for MSMF at default resolution — https://github.com/opencv/opencv/issues/27917

### Corrected

- **Nothing in this stream decoded the JPEG, and nothing else can.** FlashCap returns MJPEG as-is and its transcoder only handles YUV/NV12 → RGB, so the doc's seven tasks went from "select JPEG" straight to "apply rotation" with a compressed buffer in hand. → Added **C2a**, decoding via `Cv2.ImDecode`, which also makes C3's rotation a native `Cv2.Rotate`. Sources as above.
- **"Assume the capture path buffers rather than drops unless you make it drop"** → FlashCap drops, but it drops the **newest** frame: `QueuingProcessor.OnFrameArrived` opens with `if (this.queue.Count >= this.maxQueuingFrames) { return; }`, so arriving frames are discarded while the older queued one is still waiting. That is `DropNewest`, the inverse of this stream's requirement, and it means newest-frame-only is entirely our channel's job — https://github.com/kekyo/FlashCap/blob/main/FlashCap.Core/FrameProcessors/QueuingProcessor.cs
- **A capacity-1 `DropOldest` channel silently leaks every dropped frame's pooled buffer**, because `DropOldest` "removes and ignores" the item without disposing it. → Construct with the `itemDropped` overload and pass `frame => frame.Dispose()`. This is the most likely cause of the "flat memory" criterion failing under the slow-consumer test that is supposed to prove it. Sources as above.
- **"Pick the descriptor matching 1920 × 1080, `PixelFormats.JPEG`, 30 fps"** → you pick a `VideoCharacteristics` from within a descriptor; a descriptor is a device — https://github.com/kekyo/FlashCap/blob/main/FlashCap.Core/CaptureDeviceDescriptor.cs
- **`FramesPerSecond` compared as a number** → it is a `Fraction` (`readonly int Numerator` / `Denominator`) with an implicit `double` conversion; `== 30` does not compile against it — https://github.com/kekyo/FlashCap/blob/main/FlashCap.Core/Utilities/Fraction.cs
- **`ScanSettings.PreferredDeviceId` is `string?`, `CaptureDeviceDescriptor.Identity` is `public abstract object Identity { get; }`** → match on `Identity.ToString()` and document that as the persisted format
- **OpenCV #27917 "still open, no official fix"** → the issue is **closed** (opened 2025-10-17). The closure records no fix, so the read-back assertion stays warranted, but the doc's status line was wrong — https://github.com/opencv/opencv/issues/27917
- **"`CLAUDE.md` says its macOS backend doesn't work"** → FlashCap **added a macOS AVFoundation backend in 1.11.0** ("Mac OSX AVFoundation API is now supported"), and `FlashCap.Core/Devices/AVFoundationDevice*.cs` are in the tree. So `LoreFetch.Capture` both builds and nominally runs on macOS. But issue #182 is **open** against it: a **native crash** on capture start/refresh (callback state outliving its managed owner) plus a BGRA-vs-RGB channel mismatch producing blue-tinted frames. → macOS stays compile-only in CI; a native crash fails the host, not the test — https://github.com/kekyo/FlashCap/blob/main/README.md and https://github.com/kekyo/FlashCap/issues/182
- **One camera, one descriptor** → on Windows `EnumerateDescriptors()` concatenates DirectShow, Video for Windows **and** Media Foundation (new in 1.12.0) with no backend selector, so a single C920 yields up to three descriptors with different `Identity` values and different characteristic lists — https://github.com/kekyo/FlashCap/blob/main/FlashCap.Core/CaptureDevices.cs
- **C5's "surface these as a clean failure" assumed an exception to catch.** FlashCap cannot detect a device already in use — issue #15 is open, labelled *help wanted* and *suspended*, and the only technique in the thread is a caller-side timer. Unplug, in-use and permission-denied all present as frames that simply never arrive. → C5 now specifies a first-frame timeout and a running frame watchdog, without which "unplugging produces a clean error" is untestable because the behaviour is a hang — https://github.com/kekyo/FlashCap/issues/15
- **C6's "a lower preview resolution is the lever"** → capture resolution is fixed by the geometry budget (1080p is what yields 346 × 483 px per card), so lowering it would cut the identification stream's input. The lever is decoding *fewer frames* — drop-before-decode, plus the UI's own preview throttle.
- **The "rotation is expensive" fallback doesn't fall back to anything available.** Rendering the preview rotated via a UI transform means a consumer knows the frame is rotated, which is what *rotation lives in the source* forbids, and the preview is fed by the pipeline's `FrameProcessed`, not the source — so the knowledge would have to cross `Core/Scanning` too. Flagged as a contract change rather than a local fallback.
- **Risk 4, "the only stream that cannot be verified on the Mac"** → only the FlashCap-facing shim is. `CaptureDeviceDescriptor.Identity` is abstract but the capture entry point `InternalOpenWithFrameProcessorAsync` is `internal`, so FlashCap cannot be faked from outside its assembly — which is the argument for putting pooling, the channel, decode, rotation and the watchdogs behind an internal stage fed by `byte[]` JPEGs, where the Mac can test all of them.
- **The enumeration was described as authoritative.** Issue #52 records a Logitech Stream Cam whose 60 fps mode FlashCap's DirectShow enumeration omitted while the Windows Camera app showed it, so "absent from the list" means "this backend didn't report it" — https://github.com/kekyo/FlashCap/issues/52
- **"~100–150 lines"** → **250–350 lines plus tests**, once the missing decode stage and the two watchdogs that C5 actually requires are counted. It stays the smallest stream; it stops being a trivial one, which matters if the schedule treats it as slack.
- **"YUYV can only advertise 5 fps"** → true in effect, imprecise as stated. The bus ceiling is **5.9 fps**, and 5 is simply the highest standard UVC rate beneath it; the camera's next rung is 7.5 fps, which would need 127% of the bus. Refined in place, because the sharper version is what makes it obvious that no `set()` call can ever negotiate around it — sources as above.

### Proposed contract changes

- **`LoreFetch.Capture.csproj` package references**: add `FlashCap` 1.12.0, `OpenCvSharp4` 4.13.0.20260627, `OpenCvSharp4.runtime.win`, `OpenCvSharp4.runtime.osx.arm64`, and a logging abstraction. **Why:** FlashCap does not decode MJPEG, so this stream must, and `Cv2.ImDecode`/`Cv2.Rotate` are the decode and rotation path; the osx.arm64 runtime is needed for the off-hardware decode tests to run on the Mac. `.csproj` files are frozen and hook-enforced, so a stream cannot add these after the fork — per `CLAUDE.md`, forking with this gap stalls the stream entirely.
  **Effect on other streams:** unknown — for reconciliation.
- **A logging seam in `Core/Abstractions`** (or an agreed package reference for one). **Why:** the contract surface contains no logger, but C1 logs the characteristic list, C2 logs the negotiated format, C6 logs per-frame decode time, and three *done-when* criteria are phrased as "the log shows…". Without a seam this stream's only option is `Console.WriteLine`, and the diagnostic output C1 calls "worth keeping permanently" has nowhere to go.
  **Effect on other streams:** unknown — for reconciliation.
- **`IFrameSourceFactory` with `Task<IFrameSource> CreateAsync(ScanSettings, CancellationToken)`**. **Why:** `Description` and `Geometry` are synchronous properties that must report *negotiated* values, but negotiation is `await descriptor.OpenAsync(...)`. Either the source opens lazily — in which case both properties are wrong until the first frame and C2's "throw with the enumerated list" fires from inside the enumerator rather than at startup — or construction is async, which the contract has no shape for. The composition root lives in `LoreFetch.App`, owned by stream A, so without a factory type stream A has to name this stream's concrete class.
  **Effect on other streams:** unknown — for reconciliation.
- **A device-failure exception type in `Core/Abstractions`**, e.g. `FrameSourceException`. **Why:** `ReadAsync` can only report device loss by throwing, and the consumer needs to distinguish a genuine device failure from cancellation and from ordinary enumeration end. Today every caller would be matching on message text.
  **Effect on other streams:** unknown — for reconciliation.
- **Clarify `FrameGeometry` in the contract comments**: `Width`/`Height` are the frame **as delivered** (post-rotation, 1080 × 1920 at the default), and `RotationDegrees` records what was *already applied* rather than instructing a consumer to apply it. **Why:** rotation living in the source is the rule, but a field named `RotationDegrees` riding along with every frame is an invitation to re-apply it, and the result is the transposed-detection bug the doc lists under scrutiny. Comment-only change.
  **Effect on other streams:** unknown — for reconciliation.
- **`ScanSettings`: add a first-frame timeout and a frame-watchdog interval; document `PreferredDeviceId`'s format; constrain `CameraRotationDegrees`.** **Why:** device loss is only observable as absent frames, so the two timeouts are the mechanism C5 depends on and they should be settings rather than constants buried in `Capture`. `CameraRotationDegrees` is an unconstrained `int` while only 0/90/180/270 are implementable.
  **Effect on other streams:** unknown — for reconciliation.

### Open questions

- **Which Windows backend should this stream prefer?** 1.12.0 added Media Foundation, and `EnumerateDescriptors()` now concatenates DirectShow, Video for Windows and Media Foundation with no way to ask for one, so the C920 appears up to three times. **Recommendation: prefer DirectShow, fall back to Media Foundation, ignore Video for Windows** — DirectShow is what this doc's own latency evidence covers (1.44 s to first frame), Video for Windows is a legacy path that typically misreports modern modes, and Media Foundation earns its place as the second enumeration to try when a mode is missing (issue #52). This needs deciding rather than discovering, because `PreferredDeviceId` persists a backend-specific `Identity` and silently stops matching if the preference order changes between runs.
- **Does macOS change status now that FlashCap 1.11.0 added an AVFoundation backend?** `CLAUDE.md`'s stack note is built on the backend not existing; it exists, and issue #182 reports it crashing natively and delivering mis-channelled colour. **Recommendation: no change to the shipping decision — `win-x64` only, macOS compile-only in CI, and explicitly no device-opening test on the `macos-latest` leg.** The new fact strengthens the existing decision rather than reopening it, but the stated reason for it is now wrong, and per the review directions I have not edited `CLAUDE.md`.
- **Is OpenCvSharp acceptable as a dependency of `LoreFetch.Capture`?** `CONTRACTS.md` bars OpenCvSharp *types from the contract surface* and notes `Core` already references it, but it does not say whether a second project may. **Recommendation: yes.** It is already pinned, it is native rather than managed for both decode and rotation, and the alternatives are worse — `System.Drawing.Common` throws off-Windows, SkiaSharp would couple capture to the UI stack, and anything new cannot be added post-fork anyway. No OpenCvSharp type crosses the seam; `CameraFrame` stays a pooled `byte[]`.
- **How should a device-loss failure reach the *user*?** Throwing from `ReadAsync` faults `IScanPipeline.RunAsync`, and `IScanPipeline` exposes `FrameProcessed` and `AutoCaptured` but no error event — so the only observable is a faulted `Task` that whoever called `RunAsync` must be watching. **Recommendation: decide this in reconciliation, as it belongs to `Core/Scanning` and the UI rather than to this stream** — but it should be decided, because "camera unplugged" is the most likely runtime failure in the whole application and the current shape lets it surface as a preview that quietly stops updating.
- **Is rotation cost worth measuring at all?** I could not measure it — no code exists, and I found no primary benchmark for a 1080p BGR24 90° rotate. The concern in the research target was specifically *managed* code; with `Cv2.Rotate` this is a native transpose-and-flip, so the premise largely dissolves. **Recommendation: drop it as a planned risk and let C6's instrumentation cover it**, since the decode measurement will expose a rotation cost sitting next to it in the same per-frame log.
