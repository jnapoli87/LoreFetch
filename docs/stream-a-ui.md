# Stream A — UI

**Largest surface, lowest risk.** Builds entirely against fakes and is never blocked by another stream — not at the start, not at the end.

Owns (exclusive write access): `LoreFetch.App/**`, `LoreFetch.Core/Trigger/**`, `Tests/StreamA/**`, the stream A section of `README.md`
Consumes: `Core/Abstractions` and `Core/Scanning` (both frozen — see [`CONTRACTS.md`](CONTRACTS.md)), the five fakes (`FolderFrameSource`, `StubCardDetector`, `StubRectifier`, `StubCardIdentifier`, `StubOracleCatalog`), and every registered `ICollectionExporter`
Must not touch: `Core/Identification`, `Core/Imaging`, `Core/Collection`, `Core/Export`, `LoreFetch.Capture`, the fakes, any `.csproj`, `LoreFetch.slnx`

Avalonia **12.1.2**, CommunityToolkit.Mvvm, `Avalonia.Controls.DataGrid`. Runs on the Mac throughout — no camera, and no CV code of its own (OpenCvSharp is present only transitively, through `Core`).

**The UI talks to the scan pipeline, not to the parts.** `IScanPipeline` owns the frame source, detection, capture, thresholds and trigger calls; the UI subscribes to its events and calls `Capture()`. It never reads an `IFrameSource` or compares a distance itself.

---

## Tasks, in order

### A0 — The auto-capture trigger (`Core/Trigger`)
Implement `IAutoCaptureTrigger`: count-gated settle — exact expected count held stable for ≥ `SettleMilliseconds`, resetting on any count change or movement beyond ε, and **requiring the scene to break before re-arming**. Pure logic, no clock of its own. It's the highest-value unit-test target in the project; the required cases are in `CONTRACTS.md` terms: fires once, never re-fires on a static tableau, re-arms only after the scene breaks, never fires on a count mismatch, and a `NotifyCaptured()` suppresses an immediate re-fire. The pipeline calls it — the UI never does.

### A1 — Shell and preview surface
Handle `IScanPipeline.FrameProcessed` and blit to screen. **The frame is valid only for the duration of the callback** — convert into your own buffer inside the handler and never keep the reference; the pipeline recycles it.

**Allocate one `WriteableBitmap` and mutate it for the lifetime of the app.** Per-frame allocation is the documented cause of every "Avalonia camera preview is choppy" report — it thrashes GC with discarded frames waiting to be collected. Specifically:
- convert BGR→BGRA before blitting (a raw copy from a 3-channel buffer into a `Bgra8888` bitmap produces skewed garbage)
- respect `RowBytes` from `Lock()` — Skia pads rows, so `RowBytes != Width * 4`
- call `InvalidateVisual()`; do **not** rebind `Image.Source`, which is what triggers reallocation
- marshal via `Dispatcher.UIThread.Post` and coalesce — don't post per frame unconditionally, or a slow UI thread queues a backlog

Measured Avalonia ceiling is 60 fps @ 1080p, comfortably above the 30 fps target. Throttle preview to ~15 fps regardless; there's no reason to render faster than the eye needs.

### A2 — Quad overlay
Draw the quads from each `DetectionSnapshot` as **vector children in a `Panel` over the `Image`** — not baked into the pixel buffer. Crisper, scales with the control, hit-testable later, and costs no per-frame CPU.

### A3 — Expected-count selector
1 / 3 / 9, bound to `ScanSettings.ExpectedCount`. **Never compare a distance to a threshold** — the pipeline has already turned distances into `State` and `IsLowConfidence`; the UI renders those.

### A4 — Cohort grid
A tile per `CohortTile`: thumbnail from `RectifiedCard`, proposed oracle name, match distance. Four states per [`CONTRACTS.md`](CONTRACTS.md):

| State | Appearance |
|---|---|
| `Included` | default, clean — no marker |
| `Excluded` | **X** overlay, greyed out |
| `Unresolved` | red, no name proposed |
| `ManuallySet` | shows the corrected name, visibly distinct from a machine match |

`IsLowConfidence` adds a highlight. **It highlights only — it never gates.** A low-confidence tile still commits.

The X is deliberately the only marker: with opt-out there's nothing to affirm, so a checkmark on every tile would be noise.

### A5 — Tile interactions
All three call the tile's own methods — the UI never assigns `State` or `Chosen`.
- **Left-click** → `ToggleExcluded()`
- **Right-click** opens a context menu: *Set card manually…* → `SetManually(…)`, and *Clear* → `Clear()`, which **reverts a manual choice to the hash's own proposal** (it does not remove the tile — X does that job)
- *Set card manually…* is a **type-ahead over `IOracleCatalog.All`** (~33k). A `Contains` filter is plenty at that size — no fuzzy-matching dependency. Offer `CohortTile.Candidates` runners-up at the top of the list, since the identifier returns ranked results and the second guess is often right.

### A6 — Keyboard map — the primary interface, not an accessibility afterthought
| Key | Effect |
|---|---|
| **Space** | Capture whatever is detected now. Ignores the expected count. Replaces any pending cohort. No-op on 0 detections. |
| **Enter** | Accept the cohort — commit every non-X'd tile, clear the grid, re-arm. |
| **Escape** | Discard the cohort; nothing is written. |
| 1–9 *(if cheap)* | Toggle the X on tile N, making even the correction path mouse-free. |

The happy path is **space, enter, space, enter**. Getting this to feel instant is the difference between a tool someone uses on 500 cards and a tech demo.

⚠️ **Space and Enter must not be swallowed by a focused control.** A focused `Button` eats Space; a focused `TextBox` eats Enter. Handle these at the window level with a tunnelling handler, and verify after the type-ahead box has had focus — that's where it will break.

Space calls `IScanPipeline.Capture()`; auto captures arrive via `AutoCaptured`. Both replace any pending cohort. The pipeline calls `NotifyCaptured()` on every capture, so the UI has nothing to remember there.

### A7 — Collection view and export
`DataGrid` over `ICollectionStore.ListAsync()`, sortable by column. Export is a picker over every registered `ICollectionExporter` — show `DisplayName`, and mark any exporter whose `IsVerified` is false. **No per-format code in the UI**: it must not know the word "Moxfield". Encoding, BOM and escaping are the exporter's job (stream D), not yours.

### A8 — States that aren't the happy path
Empty collection, no frame source available, offline, and failure. **No stack traces in the UI.** An unresolved tile needs an obvious route to *Set card manually…* or users will assume the app is broken.

---

## Done when

- The app runs **on the Mac** against `FolderFrameSource` + the stubs, in all three layouts.
- The complete loop is achievable **keyboard-only**: space → enter → space → enter, repeatedly, with no mouse.
- **All four `TileState` values are reachable on demand** by configuring `StubCardIdentifier`'s distances — including `Unresolved` and `ManuallySet`.
- X toggles on and off; *Set card manually…* applies a corrected card; *Clear* reverts it to the hash's proposal.
- The trigger's unit tests cover every case in A0.
- Escape discards and writes nothing.
- Preview holds ~15 fps with **flat memory** over several minutes — the real test of A1.
- CSV opens cleanly in Excel.

## Fallbacks

- **Preview performance is bad:** drop to a lower preview resolution before reaching for `SKBitmap.InstallPixels` + `ICustomDrawOperation`. The zero-copy Skia path is available and maintainer-endorsed, but the maintainer measured *both* approaches at 60 fps @ 1080p — so if a reused `WriteableBitmap` is slow, the bug is allocation or threading, not the technique.
- **Type-ahead over 33k names is sluggish:** debounce input and cap results at ~20. Do not add a fuzzy-search dependency.
- **Number-key tile toggles run long:** cut them. They're a nicety; space/enter/escape are not.

---

## What a reviewer should scrutinise here

1. **`WriteableBitmap` lifetime.** Is exactly one allocated and mutated, or is one created per frame? This is the single most likely performance defect in the stream.
2. **Does the preview retain the `CameraFrame` past the `FrameProcessed` callback?** It must copy inside the handler. A retained reference reads a recycled pooled buffer — fine in dev, torn frames under load. (Thumbnails are safe: `RectifiedCard` is unpooled by design.)
3. **Keyboard handling with focus.** Test Space and Enter *after* the type-ahead `TextBox` has held focus. This is where it breaks.
4. **Does anything compare a distance to a number?** Only the pipeline applies thresholds.
5. **Does the UI mutate tile state directly**, rather than through `ToggleExcluded` / `SetManually` / `Clear`?
6. **Is the opt-out default genuinely `Included`?** An accidental opt-in default silently drops cards from the collection and nobody notices for a hundred scans.
7. **Threading:** is the frame consumption loop off the UI thread, and are dispatcher posts coalesced rather than per-frame?
8. **Is `IsLowConfidence` gating anything?** It must not. If it blocks a commit, throughput dies.

## Risks owned by this stream

1. **Frame retention in the preview** — see review point 2. The one genuine correctness hazard here.
2. **Keyboard focus stealing** — cheap to fix, easy to miss, destroys the core interaction.
3. **Preview allocation churn** — well-understood and documented; only a risk if A1's guidance is skipped.
4. Everything else in this stream is conventional Avalonia work with no unknowns, which is precisely why it parallelises safely.

---

## Plan review: research targets

For the pre-build stream review (see [`stream-review-directions.md`](stream-review-directions.md)). Check each against primary sources — official docs, release notes, source code — and record what you found.

1. **Avalonia 12.1.2 preview path.** Does `WriteableBitmap.Lock()` + `RowBytes` + `InvalidateVisual()` still work as described in 12.x? Is the "60 fps @ 1080p" ceiling measured on 12, or on 11?
2. **Keyboard handling.** Confirm the tunnelling-handler approach for Space/Enter in Avalonia 12, and whether a focused `Button`/`TextBox` still swallows them.
3. **Package set.** Are `CommunityToolkit.Mvvm` and `Avalonia.Controls.DataGrid` published at versions compatible with 12.1.2? Stream 0 pins every package before the fork, and nothing can be added afterwards — name any package this stream would need that isn't listed.
4. **The pipeline seam.** Can every task here be done with only `IScanPipeline`'s two events and `Capture()`? In particular: is `FrameProcessed` on a background thread workable for a 15 fps throttled preview, and does the UI need anything the pipeline doesn't expose (source `Description` for the status line, pipeline faults)?
5. **The trigger.** Is "movement beyond ε" specifiable with only `CardQuad`s and a timestamp? Propose ε, or propose the contract change needed.
