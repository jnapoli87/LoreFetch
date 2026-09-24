# LoreFetch.Tests.App

Unit tests for `LoreFetch.App` and `Core/Trigger` — view-model logic, tile-state interaction and the auto-capture trigger's state machine. The App domain's test project; see the [domain map](../../docs/CONTRACTS.md#domain-map).

## What it covers

Level 1 (unit) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): `IAutoCaptureTrigger` is called out there as the highest-value unit target in the project (fires once on settle, re-arm rule, no re-fire on a static tableau). Uses `Avalonia.Headless.XUnit` for keyboard-map and control tests — the only automated path onto Avalonia UI logic. Full visual rendering is verified manually, not here.

## Currently on `main`

`PlaceholderTests.cs` only — Stream 0's placeholder, replaced as stream A's own tests land.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/App/LoreFetch.Tests.App.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

## Internals

**Headless setup.** `TestApp.cs` configures `AvaloniaHeadlessPlatformOptions` with `UseSkia()`, `UseHeadlessDrawing = false` and `ShouldRenderOnUIThread = true`. `UseHeadlessDrawing = true` looks like the natural headless-testing default, but it is Avalonia.Headless's own no-op stub renderer — it never touches Skia and never populates a real surface, on any platform. That flag, not a macOS-ARM64 rendering gap as first assumed, was the actual cause of `CaptureRenderedFrame()` returning null: it failed identically everywhere it ran, including Windows. Worth remembering if a future headless screenshot test comes back null — check this flag before suspecting the platform.

**Screenshot output.** Every headless screenshot test saves its PNG to `AppContext.BaseDirectory` — the test project's own output directory — and never into the repository. `CLAUDE.md`'s no-card-imagery rule and the commit hook's raster-file guard both apply to anything committed; these PNGs are a local verification artifact only.

**Test files, by what they cover:**

| File | Covers |
|---|---|
| `AutoCaptureTriggerTests.cs` | `AutoCaptureTrigger` (A0) — fires once on settle, re-arms only on a count mismatch (not on movement), nearest-centroid quad matching, anchor-based (not frame-to-frame) drift measurement, max-corner-displacement movement test. |
| `AppCompositionTests.cs` | `AppComposition.ComposeAsync` — opens the frame source once and starts `RunAsync` exactly once. |
| `FrameFolderSelectionTests.cs` | `AppComposition.ChooseFrameFolder` — the three `LOREFETCH_FRAMES_DIR` code paths (valid folder, invalid folder, unset). |
| `FrameHandoffTests.cs` | `FrameHandoff` (A2) — the concurrent producer/consumer torn-frame regression: every taken buffer is uniform, sequence numbers never go backwards. |
| `PixelConvertTests.cs` | `PixelConvert.ToBgra32` (A2) — BGR24/BGRA32 conversion with padded source and destination strides. |
| `FrameToControlTransformTests.cs` | `FrameToControlTransform` (A3) — `Stretch="Uniform"` letterboxing math, including rotated (1080×1920) geometry. |
| `MainViewModelTests.cs` | `MainViewModel` (A4) — every setter writes through to `ScanSettings` immediately. |
| `ScreenshotTests.cs` | A4 controls rendered end-to-end — count selector and Auto toggle visible in a real headless-rendered window; produces `MainWindow-A4.png`. |
| `TileViewModelTests.cs` | `TileViewModel` (A5) — the four `TileState` visuals and the low-confidence highlight, reflected from the tile rather than re-derived from distance. |
| `CohortGridTests.cs` | The cohort grid's visual tree (A5) — the correct marker per tile state, plus a full screenshot render; both tiers run on every platform. |
| `TileInteractionTests.cs` | A6 — `ToggleExcludedFromUi` / `SetManuallyFromUi` / `ClearFromUi`, and the type-ahead populator (prefix length, cap, ordinal matching, runners-up first). |
| `KeyboardCaptureTests.cs` | A7 — Space/Enter/Escape via the real window-level tunnel handler, the focus bail that keeps the type-ahead typable, `AutoCaptured` marshaling, and the no-`IsDefault`-button invariant. |
| `CollectionViewTests.cs` | A8 — the collection `DataGrid` over `ListAsync`, and the exporter picker's verified/unverified badge. |
| `ErrorStateTests.cs` | A9 — empty collection, `SourceFailed` banner, store-lock retry, startup-error surface, the Unresolved "set manually" hint; asserts no banner ever carries a stack trace. |
| `LayoutFollowingCardDetectorTests.cs` | The A10-prep fake wrapper that makes the detector track `ScanSettings.ExpectedCount` live. |
| `DemoCardIdentifierTests.cs` | The A10-prep fake wrapper that cycles a captured cohort through all three hash-reachable tile states. |
| `RateCounterTests.cs` | `RateCounter` / `PreviewDiagnostics` (A10-prep) — windowed rate computation and the two counters' independence. |
| `A10CohortScreenshotTests.cs` | End-to-end: a real 9-card capture through the real `ScanPipeline`, driven by an actual Space keypress, screenshotted (`A10-cohort-9.png`). |
| `ScreenshotAssertions.cs` | Shared helpers — a saved PNG must match the window's own pixel size and must not be a single flat colour, which a blank unrendered surface would otherwise pass. |
| `PlaceholderTests.cs` | Stream 0's original placeholder; kept only to prove the project reference graph resolves. |
