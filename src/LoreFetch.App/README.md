# LoreFetch.App

The Avalonia desktop app: camera preview, capture/cohort UI, collection view and export picker. Owned by **Stream A** (worktree `stream-a`), which also owns `Core/Trigger/**` (the `IAutoCaptureTrigger` implementation) and `Tests/StreamA/**` — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## Dependencies

References `LoreFetch.Core` and `LoreFetch.Capture` (project references, both frozen at Stream 0). This project is the **composition root**: it is the only place that calls `ScanPipelineFactory.Create` and `IFrameSourceFactory.CreateAsync`, both frozen in `Core/Scanning`. It does not implement the wiring itself, and it does not reference OpenCvSharp — no contract type exposes one (see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md)), so the UI stream never writes CV code. Frames cross into the UI as `byte[]`/`CameraFrame`, converted to a single reused `WriteableBitmap`.

At integration, App wires the concrete `WebcamFrameSourceFactory` (stream C) — see the fixed name in [`../../docs/orchestration-plan.md`](../../docs/orchestration-plan.md) §4. Until then it runs against `Core/Fakes` (`FolderFrameSource` and friends).

## Stack pins and traps

Avalonia 12.1.2, `Avalonia.Controls.DataGrid` as a separate package, `CommunityToolkit.Mvvm`. See [`../../CLAUDE.md`](../../CLAUDE.md#stack-with-the-traps) for the version traps (12.0.x render-pass regression, `WriteableBitmap` allocation/`Lock()` disposal rules, xunit v3 requirement for headless tests).

## Testing

Unit-tested in `../../Tests/StreamA` (view-model logic, trigger state). Exercised end-to-end, via fakes, in `../../Tests/Integration`. UI rendering itself is verified manually, not headlessly — see [`../../docs/TESTING.md`](../../docs/TESTING.md#things-deliberately-not-tested-automatically).

Internals: documented by stream A at its done-when step.
