# LoreFetch.Capture

The FlashCap → `IFrameSource` adapter: opens the webcam, negotiates a format and yields `CameraFrame`s. Owned by **Stream C** (worktree `stream-c`), exclusive over this project and `Tests/StreamC/**` — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## Dependencies

References `LoreFetch.Core` (for `Abstractions`, notably `IFrameSource`, `IFrameSourceFactory`, `FrameSourceException`, `ScanSettings`) and is the **only project that references FlashCap** — no other project may depend on it. It also references OpenCvSharp4, but only for MJPEG decode (`Cv2.ImDecode`) and rotation; card detection and rectification stay in `Core/Imaging` (stream B). Per [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries), this stream must not touch `App` or any other `Core` folder.

Grants `InternalsVisibleTo` to `LoreFetch.Tests.StreamC` for its internal capture stage.

## Concrete name fixed for integration

`LoreFetch.Capture.WebcamFrameSourceFactory : IFrameSourceFactory`, constructed with `(ILoggerFactory)` — see [`../../docs/orchestration-plan.md`](../../docs/orchestration-plan.md) §4. The App wires this in at integration; nothing outside this project may construct it before then.

## The C920 trap

USB 2.0 bandwidth, DSHOW-over-MSMF backend preference, `FourCC` set before width/height and read back — see [`../../CLAUDE.md`](../../CLAUDE.md#the-c920-trap).

## Testing

Unit-tested in `../../Tests/StreamC`. Hardware verification (live device, sustained memory, negotiated format) is manual on the Windows PC — see [`../../docs/TESTING.md`](../../docs/TESTING.md).

Internals: documented by stream C at its done-when step.
