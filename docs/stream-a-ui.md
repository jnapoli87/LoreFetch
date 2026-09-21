# Stream A — UI

**Largest surface, lowest risk.** Builds entirely against fakes and is never blocked by another stream — not at the start, not at the end.

Owns (exclusive write access): `LoreFetch.App/**`
Consumes: `Core/Abstractions` (frozen — see [`CONTRACTS.md`](CONTRACTS.md)) and the three fakes (`FolderFrameSource`, `StubCardDetector`, `StubCardIdentifier`)
Must not touch: `Core/Identification`, `Core/Imaging`, `LoreFetch.Capture`, any `.csproj`, `LoreFetch.slnx`

Avalonia **12.1.2**, CommunityToolkit.Mvvm, `Avalonia.Controls.DataGrid`. Runs on the Mac throughout — no camera, no OpenCV.

---

## Tasks, in order

### A1 — Shell and preview surface
Consume `IFrameSource.ReadAsync()` and blit to screen.

**Allocate one `WriteableBitmap` and mutate it for the lifetime of the app.** Per-frame allocation is the documented cause of every "Avalonia camera preview is choppy" report — it thrashes GC with discarded frames waiting to be collected. Specifically:
- convert BGR→BGRA before blitting (a raw copy from a 3-channel buffer into a `Bgra8888` bitmap produces skewed garbage)
- respect `RowBytes` from `Lock()` — Skia pads rows, so `RowBytes != Width * 4`
- call `InvalidateVisual()`; do **not** rebind `Image.Source`, which is what triggers reallocation
- marshal via `Dispatcher.UIThread.Post` and coalesce — don't post per frame unconditionally, or a slow UI thread queues a backlog

Measured Avalonia ceiling is 60 fps @ 1080p, comfortably above the 30 fps target. Throttle preview to ~15 fps regardless; there's no reason to render faster than the eye needs.

### A2 — Quad overlay
Draw detected card outlines from `ICardDetector` as **vector children in a `Panel` over the `Image`** — not baked into the pixel buffer. Crisper, scales with the control, hit-testable later, and costs no per-frame CPU.

### A3 — Expected-count selector
1 / 3 / 9, bound to `ScanSettings.ExpectedCount`. **Never hardcode the distance thresholds** — `GoodDistance` and `OkDistance` arrive from stream B's calibration via `ScanSettings`.

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
- **Left-click** toggles the X (include ⇄ exclude)
- **Right-click** opens a context menu: *Set card manually…* and *Clear*
- *Set card manually…* is a **type-ahead over the oracle names already in the hash DB** (~33k). A `Contains` filter is plenty at that size — no fuzzy-matching dependency. Offer `CohortTile.Candidates` runners-up at the top of the list, since the identifier returns ranked results and the second guess is often right.

### A6 — Keyboard map — the primary interface, not an accessibility afterthought
| Key | Effect |
|---|---|
| **Space** | Capture whatever is detected now. Ignores the expected count. Replaces any pending cohort. No-op on 0 detections. |
| **Enter** | Accept the cohort — commit every non-X'd tile, clear the grid, re-arm. |
| **Escape** | Discard the cohort; nothing is written. |
| 1–9 *(if cheap)* | Toggle the X on tile N, making even the correction path mouse-free. |

The happy path is **space, enter, space, enter**. Getting this to feel instant is the difference between a tool someone uses on 500 cards and a tech demo.

⚠️ **Space and Enter must not be swallowed by a focused control.** A focused `Button` eats Space; a focused `TextBox` eats Enter. Handle these at the window level with a tunnelling handler, and verify after the type-ahead box has had focus — that's where it will break.

Every capture, manual included, must call `IAutoCaptureTrigger.NotifyCaptured()`, or auto-mode re-fires immediately on the same static scene.

### A7 — Collection view and export
`DataGrid` over `ICollectionStore.ListAsync()`, sortable by column. CSV export via `ExportCsvAsync` — **UTF-8 with BOM**, or Excel mangles non-ASCII card names.

### A8 — States that aren't the happy path
Empty collection, no frame source available, offline, and failure. **No stack traces in the UI.** An unresolved tile needs an obvious route to *Set card manually…* or users will assume the app is broken.

---

## Done when

- The app runs **on the Mac** against `FolderFrameSource` + the stubs, in all three layouts.
- The complete loop is achievable **keyboard-only**: space → enter → space → enter, repeatedly, with no mouse.
- **All four `TileState` values are reachable on demand** by configuring `StubCardIdentifier`'s distances — including `Unresolved` and `ManuallySet`.
- X toggles on and off; *Set card manually…* applies a corrected name; *Clear* removes a tile.
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
2. **`RectifiedCard` lifetime versus the grid.** `Cohort` is `IDisposable` and owns its tiles' buffers. If the UI binds a `RectifiedCard` into a long-lived list and the cohort is disposed, the thumbnail is reading freed memory — which will look fine in dev and fail during a demo. Either the UI copies thumbnails to managed bitmaps at cohort creation, or cohort disposal is deferred to the UI. **Check which, explicitly.** (Flagged as an open question in `CONTRACTS.md`.)
3. **Keyboard handling with focus.** Test Space and Enter *after* the type-ahead `TextBox` has held focus. This is where it breaks.
4. **Does anything hardcode a distance threshold** rather than reading `ScanSettings`? Stream B owns those numbers.
5. **Is `NotifyCaptured()` called on the manual path** as well as the auto path?
6. **Is the opt-out default genuinely `Included`?** An accidental opt-in default silently drops cards from the collection and nobody notices for a hundred scans.
7. **Threading:** is the frame consumption loop off the UI thread, and are dispatcher posts coalesced rather than per-frame?
8. **Is `IsLowConfidence` gating anything?** It must not. If it blocks a commit, throughput dies.

## Risks owned by this stream

1. **Cohort/tile buffer lifetime** — see review point 2. The one genuine correctness hazard here.
2. **Keyboard focus stealing** — cheap to fix, easy to miss, destroys the core interaction.
3. **Preview allocation churn** — well-understood and documented; only a risk if A1's guidance is skipped.
4. Everything else in this stream is conventional Avalonia work with no unknowns, which is precisely why it parallelises safely.
