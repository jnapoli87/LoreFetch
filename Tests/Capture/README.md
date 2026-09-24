# LoreFetch.Tests.Capture

Unit tests for `LoreFetch.Capture` — the FlashCap → `IFrameSource` adapter. The Capture domain's test project; see the [domain map](../../docs/CONTRACTS.md#domain-map).

## What it covers

Level 1 (unit) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): format negotiation, rotation application, frame ownership/disposal. `LoreFetch.Capture` grants this project `InternalsVisibleTo` for its internal capture stage, so this project's assembly name must keep matching what `Capture.csproj` declares. Hardware-dependent cases (live device, sustained memory) are manual, on the Windows PC, per [`../../docs/TESTING.md`](../../docs/TESTING.md#hardware--manual) — not run here or in CI.

## What's covered

Every case runs against synthetic data or a plain in-memory double — no camera, no committed imagery:

- **C1a — channel/pool** (`JpegFrameChannelTests.cs`): `JpegFrameChannel`'s newest-frame-only semantics (capacity 1, `DropOldest`), that a dropped frame's pooled buffer is disposed immediately rather than leaked, that a slow consumer against a fast producer keeps peak outstanding rentals bounded, and that a second concurrent enumeration throws `InvalidOperationException` rather than silently blocking.
- **C1b — decode/rotate** (`JpegFrameDecoderTests.cs`): `JpegFrameDecoder` against procedurally-drawn synthetic JPEGs (a marker block in one corner) for all four `CameraRotationDegrees` values, asserting both the reported `Geometry` and where the marker actually lands.
- **C1c — watchdogs** (`FrameWatchdogTests.cs`): `FrameWatchdog`'s two independent timers — first-frame timeout vs. the recurring frame timeout — pinned so a regression that applies the wrong one still fails even though "an exception eventually arrives" either way; the exception message naming all three candidate causes (unplugged, in use, permission denied).
- **C2 — selection** (`CaptureDeviceSelectorTests.cs`, `CaptureDescriptorFormattingTests.cs`, `WebcamFrameSourceFactoryTests.cs`): format negotiation over plain `CaptureDescriptor`/`CaptureCharacteristic` data (no FlashCap type anywhere in these files), `PreferredDeviceId`'s backend-prefixed string format, and that `Description` reports what was actually negotiated rather than the requested constants.
- **C4-fix — harness helper** (`Hardware/HardwareTestSupportMoveNextBoundTests.cs`): non-hardware unit proof for `HardwareTestSupport.MoveNextWithHarnessBoundAsync`, the shared helper the hardware tests below use for every per-`MoveNextAsync` bound — see that class's doc comment.

`LoreFetch.Capture` grants this project `InternalsVisibleTo` for its internal capture stage (`JpegFrameChannel`, `JpegFrameDecoder`, `CaptureDeviceSelector`, `CaptureDescriptorFormatting`), so this project's assembly name must keep matching what `Capture.csproj` declares.

## Running

```sh
scripts/lorefetch.sh test
# or directly, filtering out the hardware tests below:
dotnet test Tests/Capture/LoreFetch.Tests.Capture.csproj -c Release --filter "Category!=Hardware"
```

`scripts/lorefetch.sh test` already applies that filter. **Do not run `dotnet test` on this project unfiltered** — without `Category!=Hardware` it also runs `Hardware/UnplugCameraTests.cs`, which opens the real camera and then waits up to 120 s for a human to physically unplug it.

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

### Hardware tests (Windows PC + C920)

Manual, per [`../../docs/TESTING.md`](../../docs/TESTING.md#hardware--manual) — never run in CI, never run as part of this stream's own "run the suite" step. Two files, `Hardware/HardwareCameraTests.cs` and `Hardware/UnplugCameraTests.cs`, both open the real device through the public `WebcamFrameSourceFactory` — nothing internal is reached into.

Unattended (negotiation, sustained memory, slow-consumer latency, reopen-after-dispose — no human needed once started):

```sh
dotnet test Tests/Capture/LoreFetch.Tests.Capture.csproj -c Release --filter "Category=Hardware&Interactive!=Unplug"
```

Interactive (waits for a person to physically unplug the camera on cue):

```sh
dotnet test Tests/Capture/LoreFetch.Tests.Capture.csproj -c Release --filter "FullyQualifiedName~UnplugCameraTests.UnplugProducesCleanErrorWithinWatchdog"
```

Environment variables, all optional:

| Variable | Default | Effect |
|---|---|---|
| `LOREFETCH_HW_OUT` | `%TEMP%\lorefetch-hw` | Where per-test logs and `last-frame.png` are written. |
| `LOREFETCH_HW_MINUTES` | `3` | Duration of `HardwareCameraTests.SustainedRunKeepsMemoryFlat`. |
| `LOREFETCH_HW_UNPLUG_WAIT_S` | `120` | How long `UnplugCameraTests` waits for the physical unplug before giving up. |

Output — logs and `last-frame.png` — always goes to `%TEMP%\lorefetch-hw` (or `LOREFETCH_HW_OUT` if set), **never into the repo**: a saved frame is card-camera imagery, and CLAUDE.md's "never commit card imagery" rule applies regardless of who took the photo.

Internals: `HardwareTestSupport.cs` bundles the logging plumbing (an `ILoggerFactory` that fans every line out to `ITestOutputHelper`, a log file, and an in-memory queue a test can grep) and `MoveNextWithHarnessBoundAsync`, the shared helper every per-`MoveNextAsync` read in both hardware test classes goes through — see its doc comment in that file for the defect it exists to prevent (a harness-side timeout shorter than the product's own watchdogs, followed by disposing an enumerator while its `MoveNextAsync` is still pending, which throws `NotSupportedException` and masks whatever the product would otherwise have reported).
