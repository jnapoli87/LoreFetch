# Stream A — UI

**Largest surface, lowest risk.** Builds entirely against fakes and is never blocked by another stream — not at the start, not at the end.

Owns (exclusive write access): `LoreFetch.App/**`, `LoreFetch.Core/Trigger/**`, `Tests/StreamA/**`, the stream A section of `README.md`
Consumes: `Core/Abstractions` and `Core/Scanning` (both frozen — see [`CONTRACTS.md`](CONTRACTS.md)), the five fakes (`FolderFrameSource`, `StubCardDetector`, `StubRectifier`, `StubCardIdentifier`, `StubOracleCatalog`), and every registered `ICollectionExporter`
Must not touch: `Core/Identification`, `Core/Imaging`, `Core/Collection`, `Core/Export`, `LoreFetch.Capture`, the fakes, any `.csproj`, `LoreFetch.slnx`

Avalonia **12.1.2**, CommunityToolkit.Mvvm, `Avalonia.Controls.DataGrid`. Runs on the Mac throughout — no camera, and no CV code of its own (OpenCvSharp is present only transitively, through `Core`).

All three are published and mutually compatible, verified on the NuGet v3 API: `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Controls.DataGrid` / `Avalonia.Headless.XUnit` all at **12.1.2** (the current release), `CommunityToolkit.Mvvm` at **8.4.2** (MIT, `netstandard2.0` + `net8.0`). Three things Stream 0 has to know before it pins, since nothing can be added after the fork:

- **Avalonia 12.1.2 targets `net8.0` and `net10.0` only** — `net6.0` and `netstandard2.0` are gone as of 12. The App's TFM has to be one of those two.
- **`Avalonia.Headless.XUnit` 12.1.2 exists and is the only way to test A6's keyboard map automatically.** It is not in the package list above. Name it now or the keyboard map is hand-tested forever.
- **`Avalonia.Diagnostics` has no 12.x release at all** (it stops at 11.3.22). DevTools moved out of the repo: `this.AttachDevTools()` is now `this.AttachDeveloperTools()` from the `AvaloniaUI.DiagnosticsSupport` package, plus an out-of-process `AvaloniaUI.DeveloperTools` dotnet tool. See *Open questions* — there is an unresolved question about whether it is free.

**The UI talks to the scan pipeline, not to the parts.** `IScanPipeline` owns the frame source, detection, capture, thresholds and trigger calls; the UI subscribes to its events and calls `Capture()`. It never reads an `IFrameSource` or compares a distance itself.

---

## Tasks, in order

### A0 — The auto-capture trigger (`Core/Trigger`)
Implement `IAutoCaptureTrigger`: count-gated settle — exact expected count held stable for ≥ `SettleMilliseconds`, resetting on any count change or movement beyond ε, and **requiring the scene to break before re-arming**. Pure logic, no clock of its own. It's the highest-value unit-test target in the project; the required cases are in `CONTRACTS.md` terms: fires once, never re-fires on a static tableau, re-arms only after the scene breaks, never fires on a count mismatch, and a `NotifyCaptured()` suppresses an immediate re-fire. The pipeline calls it — the UI never does.

**ε is specifiable with only what `Evaluate` receives** — `CardQuad`s are in frame coordinates and carry all four corners, so the trigger keeps the previous snapshot's quads and compares corner positions. Two details decide whether it works:

- **Match quads between frames by nearest centroid, not by index.** `ICardDetector` orders by descending area, and two cards of near-identical area can swap places between frames — index-matching would read that swap as movement and reset the settle forever.
- **ε = 4 px** as the starting value. At the settled ~9.75″ mount height that is 1360/9.75 ≈ 139 px/inch, so 4 px ≈ 0.03″ — above typical Canny/contour corner jitter of 1–3 px, and far below any real hand movement. Tune against a real camera, not a fixture.

Two things A0 needs that the frozen surface does not currently provide — both under *Proposed contract changes*: **ε has nowhere to live in `ScanSettings`**, and **there is no `AutoCaptureEnabled` flag**, so auto mode cannot be turned off at all (the interaction model in `CLAUDE.md` says Space "ignores the expected count *even in auto mode*", which presupposes a mode that can be off).

Define "the scene breaks" explicitly, because the re-arm rule is the whole defence against a static tableau re-firing: **any snapshot whose quad count differs from `expectedCount`**, zero included. A tableau that stays correct and still forever is armed exactly once.

Note also that `now` is a `DateTimeOffset` supplied by the caller, so it is only as monotonic as the pipeline's clock — see *Open questions*.

### A1 — Shell and preview surface
Handle `IScanPipeline.FrameProcessed` and blit to screen. **The frame is valid only for the duration of the callback** — convert into your own buffer inside the handler and never keep the reference; the pipeline recycles it.

**Allocate one `WriteableBitmap` and mutate it for the lifetime of the app.** Per-frame allocation is the documented cause of every "Avalonia camera preview is choppy" report — it thrashes GC with discarded frames waiting to be collected. Specifically:
- convert BGR→BGRA before blitting (a raw copy from a 3-channel buffer into a `Bgra8888` bitmap produces skewed garbage). **Switch on `CameraFrame.Layout`, don't assume `Bgr24`** — `PixelLayout` has two members and the source chooses, so a `Bgra32` source needs a stride-respecting copy and no channel expansion. The same applies to `RectifiedCard.Layout` for the A4 thumbnails.
- **`Lock()` → write → dispose the lock, every frame.** Not optional bookkeeping: the Skia backend caches an `SKImage` snapshot of the bitmap and invalidates it in exactly one place — `BitmapFramebuffer.Dispose()`, which calls `NotifyPixelsChanged()` and clears `_imageValid`. Writing through a pointer cached from an earlier `Lock()` and then calling `InvalidateVisual()` renders the *first* frame forever ([`WriteableBitmapImpl.cs`](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Skia/Avalonia.Skia/WriteableBitmapImpl.cs); snapshot introduced in [#18164](https://github.com/AvaloniaUI/Avalonia/pull/18164), symptom and the "lock / write / unlock" answer in [#19030](https://github.com/AvaloniaUI/Avalonia/issues/19030)). Keep the locked region to the memcpy alone — `Lock()` and `Draw()` take the same monitor, so holding the framebuffer blocks the render thread.
- respect `RowBytes` from `Lock()`: it is only guaranteed `>= Width * bytesPerPixel` ([`RetainedFramebuffer.cs` L29](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Platform/RetainedFramebuffer.cs#L29) validates just that lower bound, and Avalonia's own row-by-row copy steps the destination by `RowBytes`). Not because Skia pads — on the Skia backend `RowBytes` comes from `SKImageInfo.RowBytes` and is unpadded — but because row padding is an allowance of the `ILockedFramebuffer` contract that other backends take.
- call `InvalidateVisual()` **on the `Image`**, after disposing the lock. Nothing invalidates for you: `Image` declares `AffectsRender<Image>(SourceProperty, …)`, so invalidation is tied to the *property* changing, not to pixel content ([`Image.cs` L47](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Image.cs#L47); maxkatz6 in [#9835](https://github.com/AvaloniaUI/Avalonia/issues/9835#issuecomment-1368653281): *"WriteableBitmap doesn't invalidate container controls, and developers need to call image.InvalidateVisual() manually"* — `WriteableBitmap.Invalidate` still does not exist at 12.1.2). Do **not** rebind `Image.Source`: the same instance raises no property change and is a no-op, and a new instance also trips `AffectsMeasure` and adds a full measure/arrange pass per frame.
- marshal off the pipeline thread and **coalesce**. Prefer [`TopLevel.RequestAnimationFrame`](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/TopLevel.cs#L606) — render-loop paced, so it coalesces by construction; otherwise keep the `DispatcherOperation` returned by `InvokeAsync` and `Abort()` the stale one rather than queueing N frames. A bare `Dispatcher.UIThread.Post` per frame is exactly what queues a backlog on a slow UI thread.

Throttle preview to ~15 fps; there's no reason to render faster than the eye needs. **"60 fps @ 1080p" is not a measured ceiling** — see *Corrected*. The nearest real data point is kekekeks reporting a full 60 FPS from this exact reused-`WriteableBitmap` recipe on Avalonia 11.0.0-rc1, resolution unstated ([#11636](https://github.com/AvaloniaUI/Avalonia/issues/11636#issuecomment-1574949733)); on Windows 60 fps is normally the vsync/composition cap rather than a bitmap-path limit. 30 fps is comfortably inside that envelope and 15 fps more so, so the *conclusion* holds even though the number was never measured.

⚠ **There is no official Avalonia sample of mutating a `WriteableBitmap` behind an `Image`.** The repo's only sample, [`RenderDemo/Pages/WriteableBitmapPage.cs`](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/samples/RenderDemo/Pages/WriteableBitmapPage.cs), uses a custom `Control` with a `Render` override and `context.DrawImage(...)`. That is the shape to fall back to if the `Image` route misbehaves — strictly less magic, not more.

### A2 — Quad overlay
Draw the quads from each `DetectionSnapshot` as **vector children in a `Panel` over the `Image`** — not baked into the pixel buffer. Crisper, scales with the control, hit-testable later, and costs no per-frame CPU.

Quads are in frame coordinates, so the overlay needs the frame→control transform, which means accounting for the `Image`'s `Stretch` (letterboxing under `Uniform`) as well as the scale factor.

⚠ **`FrameGeometry` is ambiguous about rotation, and this task is where it bites.** `CONTRACTS.md` says rotation lives in the source — `WebcamFrameSource` applies it so everything downstream sees an upright frame — yet `FrameGeometry` carries `Width`, `Height` *and* `RotationDegrees`. If `Width`/`Height` are the pre-rotation sensor dimensions, every overlay quad is mapped through a transposed frame at the settled 90° rotation and lands nowhere near its card. Resolve before Stream 0 freezes: see *Open questions*.

### A3 — Expected-count selector
1 / 3 / 9, bound to `ScanSettings.ExpectedCount`. **Never compare a distance to a threshold** — the pipeline has already turned distances into `State` and `IsLowConfidence`; the UI renders those.

`ScanSettings` is a plain mutable class with no `INotifyPropertyChanged`, so bind the selector to a view-model property that writes through to it rather than to the settings object directly. The cross-thread write is benign — the UI thread writes `ExpectedCount` while the pipeline thread reads it, and `int` reads and writes are atomic — but it is a write the pipeline will observe on some later frame, not immediately. This selector is also where an **auto-capture on/off toggle** belongs, once `ScanSettings` has somewhere to put it (*Proposed contract changes*).

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

`CohortTile` has no `INotifyPropertyChanged` either, so wrap each tile in a tile view-model: call the tile's method, then raise the notifications for `State` / `Chosen` / `ChosenDistance` from the wrapper. This is safe precisely because the UI is the *only* mutator — the pipeline constructs tiles and never touches them again — so the wrapper can never miss a change it didn't cause. Binding a `CohortTile` straight into the grid would render the initial state and then never update, which looks exactly like broken click handling.
- **Left-click** → `ToggleExcluded()`
- **Right-click** opens a context menu: *Set card manually…* → `SetManually(…)`, and *Clear* → `Clear()`, which **reverts a manual choice to the hash's own proposal** (it does not remove the tile — X does that job)
- *Set card manually…* is a **type-ahead over `IOracleCatalog.All`** (~33k). A `Contains` filter is plenty at that size — no fuzzy-matching dependency. Offer `CohortTile.Candidates` runners-up at the top of the list, since the identifier returns ranked results and the second guess is often right. `AutoCompleteBox` is in the core `Avalonia` package, nothing extra to reference, and its Fluent theme comes in with `<FluentTheme />` — no per-control `StyleInclude`.
  ⚠ **"A `Contains` filter is plenty at that size" is wrong with `AutoCompleteBox`'s defaults, and the debounce-and-cap fallback has to be promoted into the task.** `RefreshView()` is an uncapped linear scan over a full eager `List<object>` copy of all 33k entries, and the registered defaults are `MinimumPrefixLength = 1` and `MinimumPopulateDelay = TimeSpan.Zero` ([`AutoCompleteBox.Properties.cs`](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/AutoCompleteBox/AutoCompleteBox.Properties.cs), [`RefreshView()` L1331](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/AutoCompleteBox/AutoCompleteBox.cs#L1331)) — so out of the box, typing one letter scans all 33k synchronously on the UI thread, per keystroke, with no debounce and no cap on what it pushes into the popup's list. Rendering itself is safe (the popup is a virtualized `ListBox` and the Fluent theme caps `MaxDropDownHeight`); the filter pass and list churn are not. So set **`AsyncPopulator`** — the only hook that permits a cap, and it takes a `CancellationToken` so keystroke N cancels N−1 — filter off the UI thread with a `.Take(~20)`, raise `MinimumPrefixLength` to 2–3, and set `MinimumPopulateDelay` to ~150 ms. Prefer `StartsWithOrdinal` over the default `StartsWith` (no culture work). Note `AsyncPopulator` *replaces* the built-in `ItemsSource`/`TextFilter` scan rather than complementing it.
  ⚠ `StubOracleCatalog` holds "a few hundred entries", so **this stream cannot exercise the 33k path against the fakes at all** — the one performance claim in A5 is the one the stubs can't test. See *Proposed contract changes*.

### A6 — Keyboard map — the primary interface, not an accessibility afterthought
| Key | Effect |
|---|---|
| **Space** | Capture whatever is detected now. Ignores the expected count. Replaces any pending cohort. No-op on 0 detections. |
| **Enter** | Accept the cohort — commit every non-X'd tile, clear the grid, re-arm. |
| **Escape** | Discard the cohort; nothing is written. |
| 1–9 *(if cheap)* | Toggle the X on tile N, making even the correction path mouse-free. |

The happy path is **space, enter, space, enter**. Getting this to feel instant is the difference between a tool someone uses on 500 cards and a tech demo.

⚠️ **Space and Enter must not be swallowed by a focused control.** The window-level tunnelling handler is the right mechanism and it does work: `KeyDownEvent` is registered `RoutingStrategies.Tunnel | RoutingStrategies.Bubble` ([`InputElement.cs` L106](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Input/InputElement.cs#L106)), the tunnel pass runs root→target before the bubble pass, and control `OnKeyDown` overrides are bubble-only — so `window.AddHandler(InputElement.KeyDownEvent, h, RoutingStrategies.Tunnel)` sees the key first. The explicit `RoutingStrategies.Tunnel` argument is required; `AddHandler`'s own default is `Direct | Bubble`.

But the specifics the warning states are wrong in both directions, and the real hazard is a different one:

- **A focused `Button` eats Space only when focused, and eats Enter unconditionally** ([`Button.cs` L297](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Button.cs#L297)). Worse, a button with `IsDefault="True"` registers its own handler *on the input root* and fires on Enter even when it doesn't have focus. **Set `IsDefault` on no button in this app** — Enter is the commit key.
- **A single-line `TextBox` does not eat Enter.** `TextBox.OnKeyDown` marks Enter handled only `if (AcceptsReturn)` ([`TextBox.cs` L1395](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/TextBox.cs#L1395)), and the type-ahead box is single-line. It also never marks Space handled — the space *character* arrives through the separate `TextInput` event, not `KeyDown`.
- ⚠ **The actual trap.** On Win32, `_ignoreWmChar = e.Handled` after `WM_KEYDOWN` ([`WindowImpl.AppWndProc.cs` L983](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Windows/Avalonia.Win32/WindowImpl.AppWndProc.cs#L983)), so a window-level tunnel handler that marks **Space** `Handled` suppresses the following `WM_CHAR` — and the focused type-ahead box never receives the space character. *"Black Lotus"* becomes untypeable. The global handler **must bail out when focus is in a text-entry control** (`FocusManager.GetFocusedElement() is TextBox`), returning without setting `Handled`. **And it reproduces on the Mac**, so it is testable where this stream actually develops: `AvnView.mm`'s `-keyDown:` returns early when user code handled the event, skipping both `[[self inputContext] handleEvent:]` and `RawTextInputEvent` ([L795](https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/native/Avalonia.Native/src/OSX/AvnView.mm#L795), whose comment cites the Win32 parity directly). Different mechanism, same symptom — which is why A6's "verify after the type-ahead box has had focus" is the right instinct but doesn't need a PC to act on.
- **`KeyBindings` beat even the tunnel phase** — `KeyboardDevice` walks the focused element's ancestors invoking matching `KeyBindings` before raising the routed event at all. Don't mix a `KeyBinding` for Space/Enter with the tunnel handler; pick one.

Space calls `IScanPipeline.Capture()`; auto captures arrive via `AutoCaptured`. Both replace any pending cohort. The pipeline calls `NotifyCaptured()` on every capture, so the UI has nothing to remember there.

Two threading facts the contract leaves unstated, both of which land on this task:

- **`Capture()` gets called from a key handler on the UI thread, while the pipeline thread is replacing and disposing the very frame it reads.** `CONTRACTS.md` says the pipeline "holds exactly one frame … and disposes it when the next one replaces it", but never says `Capture()` is safe to call concurrently with that. As specified, Space can rectify a buffer that has just gone back to the pool. *Proposed contract changes.*
- **`Capture()` is synchronous and does the rectify-and-identify work inline** — up to nine `warpPerspective`s plus nine index lookups. On the UI thread that is a visible freeze on every Space, which is the opposite of "getting this to feel instant". The fix is to run it off the UI thread, which needs the same thread-safety answer.
- `AutoCaptured` is not documented as raised on the UI thread (only `FrameProcessed` says which thread it uses), so **assume background and marshal before touching the grid.**

### A7 — Collection view and export
`DataGrid` over `ICollectionStore.ListAsync()`, sortable by column. Export is a picker over every registered `ICollectionExporter` — show `DisplayName`, and mark any exporter whose `IsVerified` is false. **No per-format code in the UI**: it must not know the word "Moxfield". Encoding, BOM and escaping are the exporter's job (stream D), not yours.

**`Avalonia.Controls.DataGrid` is deprecated as of 12.x but is still the right choice here, because of the "sortable by column" requirement.** Its readme now says *"`DataGrid` is deprecated and only receives bug fixes … we recommend using TableView"* ([readme at 12.1.2](https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid/blob/12.1.2/readme.md)), and `TableView` is new in core Avalonia 12.1.0 with no extra package and no `StyleInclude`. But `TableView` **has no sorting at all, by design** — MrJul scoped it out in [#21237](https://github.com/AvaloniaUI/Avalonia/issues/21237): *"No sorting, grouping or filtering or column re-ordering"* — and its column header has no click handler to hook. `DataGrid` has `CanUserSortColumns`, `DataGridColumn.CanUserSort`, `SortMemberPath` and `CustomSortComparer`. So: keep `DataGrid`, and keep its theme include, which is still required in 12.x:
`<StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />`.
Reach for `TableView` only if column sorting gets cut, in which case it virtualizes rows for free (it derives from `ListBox`).

🚧 **This is the one task in the stream that is genuinely blocked, and the blockage contradicts the premise.** `CONTRACTS.md` promises "stream A never needs anything real from B, C or D — not at the start, not at the end", but the five fakes cover only the scan path: **there is no fake `ICollectionStore` and no fake `ICollectionExporter`.** `Core/Collection` and `Core/Export` are stream D's, and this stream must not touch them. So A7 and A8's empty-collection state have nothing to run against, and nothing can be added after the fork. See *Proposed contract changes* — this is the highest-priority item in this review.

### A8 — States that aren't the happy path
Empty collection, no frame source available, and failure. **No stack traces in the UI.** An unresolved tile needs an obvious route to *Set card manually…* or users will assume the app is broken.

**"Offline" is not a state this app has.** LoreFetch is fully offline by design; the only network access in the project is the Scryfall bulk pull, which happens in `LoreFetch.Lab` at index-build time and never in the shipped app. The startup failures that *are* real, and that this stream has to render, are **the hash index or the thresholds file missing or unreadable** — and note that nothing in `CONTRACTS.md` says who loads the thresholds file into `ScanSettings` at startup, even though startup lives in `LoreFetch.App`. See *Open questions*.

Pipeline failure reaches the UI through the `Task` returned by `RunAsync` — whoever starts it observes the fault — which is workable but depends on the composition question under *Proposed contract changes*. Recovery (frame source dies, user retries) means disposing the pipeline and building a new one, which needs that same answer.

---

## Done when

- The app runs **on the Mac** against `FolderFrameSource` + the stubs, in all three layouts.
- The complete loop is achievable **keyboard-only**: space → enter → space → enter, repeatedly, with no mouse.
- **All four `TileState` values are reachable on demand** — but only `Included` (with and without `IsLowConfidence`) and `Unresolved` come from configuring `StubCardIdentifier`'s distances. `Excluded` and `ManuallySet` are reachable *only* by user action, since nothing but `ToggleExcluded()` and `SetManually()` produces them. Stated as written, this criterion can't be met by distance configuration alone.
- X toggles on and off; *Set card manually…* applies a corrected card; *Clear* reverts it to the hash's proposal.
- The trigger's unit tests cover every case in A0.
- Escape discards and writes nothing.
- Preview holds ~15 fps with **flat memory** over several minutes — the real test of A1.
- ~~CSV opens cleanly in Excel.~~ **Not this stream's criterion.** Encoding, BOM and escaping are stream D's (A7 says so itself), and stream A has no `ICollectionStore` implementation to write a CSV with — see *Proposed contract changes*. Belongs in stream D's done-when or at integration.

## Fallbacks

- **Preview performance is bad:** drop to a lower preview resolution first. The advice not to reach for `SKBitmap.InstallPixels` + `ICustomDrawOperation` stands, but **not for the reason given** — nobody measured both approaches, and the zero-copy path is not maintainer-endorsed (*Corrected*). Better reasons to leave it alone: maxkatz6's actual position is *"I don't know if it will result in noticeably better perf, but you can try it out"* ([#8120](https://github.com/AvaloniaUI/Avalonia/discussions/8120#discussioncomment-2740487)), and kekekeks rejected an `SKImage`-over-mutable-`SKPixmap` implementation as *"strictly invalid … the docs explicitly specify that the underlying pixmap should be unchanged until SKImage is destroyed"* ([#17717](https://github.com/AvaloniaUI/Avalonia/pull/17717#issuecomment-2532422090)) — which is precisely the aliasing a live camera preview would do. If a reused `WriteableBitmap` is slow, suspect allocation, threading, or a missing lock-dispose (A1) before the technique. The supported fallback shape is the custom `Control` + `Render` override that `RenderDemo` uses.
- ~~**Type-ahead over 33k names is sluggish:** debounce input and cap results at ~20.~~ **Not a fallback — a requirement.** `AutoCompleteBox`'s defaults debounce nothing and cap nothing, so this is A5's baseline configuration rather than a remedy held in reserve. "Do not add a fuzzy-search dependency" still stands.
- **Number-key tile toggles run long:** cut them. They're a nicety; space/enter/escape are not.

---

## What a reviewer should scrutinise here

1. **`WriteableBitmap` lifetime.** Is exactly one allocated and mutated, or is one created per frame? This is the single most likely performance defect in the stream. **And: is the `Lock()` disposed every frame?** A cached framebuffer pointer plus `InvalidateVisual()` displays frame 1 forever — a correctness bug that looks like a frozen camera, not a slow one (A1).
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

---

## Plan review findings — 2026-09-21

Package facts come from the NuGet v3 flat-container API; Avalonia behaviour from the source at tag `12.1.2` and from maintainer comments, preferred over docs where the docs are silent (which, for the `WriteableBitmap`-behind-an-`Image` recipe, they entirely are).

### Verified

- **Avalonia 12.1.2 is the current release**, and `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Skia`, `Avalonia.Headless`, `Avalonia.Headless.XUnit` and `Avalonia.Controls.DataGrid` are all published at exactly 12.1.2 — https://api.nuget.org/v3-flatcontainer/avalonia/index.json and https://api.nuget.org/v3-flatcontainer/avalonia.controls.datagrid/index.json
- **`CommunityToolkit.Mvvm` 8.4.2**, MIT, ships `netstandard2.0`/`netstandard2.1`/`net8.0` — compatible with Avalonia 12's TFMs — https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/index.json
- **Avalonia 12.1.2 targets `net8.0` and `net10.0` only** (11.3.x had net6.0/net8.0/netstandard2.0) — `Avalonia.nuspec` inside the 12.1.2 package; https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
- **`WriteableBitmap.Lock()` returns `ILockedFramebuffer`** exposing `Address`, `RowBytes`, `Size`, `Format`, `Dpi` and (new in 12) `AlphaFormat` — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Platform/ILockedFramebuffer.cs
- **`RowBytes` is only guaranteed `>= Width * bytesPerPixel`** — the framework validates just that lower bound and its own copy loop steps by `RowBytes` — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Platform/RetainedFramebuffer.cs#L29
- **`InvalidateVisual()` on the `Image` is required and is the endorsed route**; `Image` invalidates on `SourceProperty` changing, not on pixel content — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Image.cs#L47 and maxkatz6 in https://github.com/AvaloniaUI/Avalonia/issues/9835#issuecomment-1368653281
- **The tunnelling-handler approach works in Avalonia 12.** `KeyDownEvent` is registered `RoutingStrategies.Tunnel | RoutingStrategies.Bubble`, the tunnel pass runs root→target before bubbling, and control `OnKeyDown` overrides are bubble-only — so a window-level tunnel handler sees Space/Enter first — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Input/InputElement.cs#L106
- **`Dispatcher.UIThread.Post(Action, DispatcherPriority)` exists**, and `TopLevel.RequestAnimationFrame` is available as a render-loop-paced (self-coalescing) alternative — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Base/Threading/Dispatcher.Invoke.cs#L616, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/TopLevel.cs#L606
- **`AutoCompleteBox` is in the core `Avalonia` package** (`Avalonia.Controls.dll`), themed by `<FluentTheme />` with no per-control include — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/AutoCompleteBox/AutoCompleteBox.cs
- **`Avalonia.Controls.DataGrid` 12.x still needs its own theme include**, `avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml` — https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid/blob/12.1.2/src/DataGridSample/App.axaml
- **`DataGrid` retains the full sorting apparatus** (`CanUserSortColumns`, `DataGridColumn.CanUserSort`, `SortMemberPath`, `CustomSortComparer`), which `TableView` does not have — https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid/blob/12.1.2/src/Avalonia.Controls.DataGrid/DataGrid.cs
- **Staying on 12.1.x matters more than the `OpenCvSharp5.AvaloniaExtensions` pin implies.** Avalonia 12.0.x lost 5–40× render-pass CPU to region dirty-rect clipping (≈70 ms/frame at 1440p) — fixed in 12.1.x, which defaults to the safe path — https://github.com/AvaloniaUI/Avalonia/issues/21538#issuecomment-4679543248
- **ε is specifiable from `Evaluate`'s arguments alone.** `CardQuad` carries all four corners in frame coordinates, so the trigger can diff against the previous snapshot; no contract change needed for the *mechanism* (only for where the value lives).

### Corrected

- **A1: "respect `RowBytes` from `Lock()` — Skia pads rows"** → honouring `RowBytes` is right, the reason is wrong. On the Skia backend `RowBytes` comes from `SKImageInfo.RowBytes` and is *unpadded*; padding is an allowance of the `ILockedFramebuffer` contract that other backends take. Keep the stride-correct loop, drop the justification — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Skia/Avalonia.Skia/WriteableBitmapImpl.cs
- **A1 omitted the step that actually makes mutation visible.** The Skia backend caches an `SKImage` snapshot invalidated *only* by `BitmapFramebuffer.Dispose()`. Writing through a pointer cached from an earlier `Lock()` and calling `InvalidateVisual()` renders the first frame forever. The recipe is `Lock()` → write → **dispose the lock** → `InvalidateVisual()` — https://github.com/AvaloniaUI/Avalonia/pull/18164 and https://github.com/AvaloniaUI/Avalonia/issues/19030
- **A1: "do not rebind `Image.Source`, which is what triggers reallocation"** → reallocation is not the mechanism. Re-assigning the *same* instance raises no property change and is a silent no-op (so it cannot work as a refresh); a *new* instance also trips `AffectsMeasure` and adds a measure/arrange pass per frame — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Image.cs#L47
- **A1: "Measured Avalonia ceiling is 60 fps @ 1080p"** → no such measurement exists. The nearest primary source is kekekeks (org member) reporting "full 60 FPS" from this recipe on **11.0.0-rc1 with the resolution unstated** — https://github.com/AvaloniaUI/Avalonia/issues/11636#issuecomment-1574949733 — and on Windows 60 fps is normally the vsync/composition cap, not a bitmap-path limit — https://github.com/AvaloniaUI/Avalonia/issues/21053#issuecomment-4167457254. The conclusion (30 fps is comfortable) survives; the number should not be quoted as measured. **This claim also appears in `CLAUDE.md`'s Avalonia-preview note and should be fixed there in reconciliation.**
- **Fallbacks: "the maintainer measured *both* approaches at 60 fps @ 1080p"** → the source is stevemonaco, a `CONTRIBUTOR` and not an AvaloniaUI org member, saying the opposite: *"I haven't benchmarked the two approaches … so you can't really compare them. They're also both animated differently (hitting 60fps 1080p, but not much higher)"* — https://github.com/AvaloniaUI/Avalonia/discussions/12675#discussioncomment-6837012. The inference built on it ("if a reused `WriteableBitmap` is slow, the bug is allocation or threading, not the technique") is therefore unsupported as stated, though still a reasonable first suspicion — and now has a third candidate, the missing lock-dispose.
- **Fallbacks: "the zero-copy Skia path is available and maintainer-endorsed"** → not endorsed. maxkatz6's actual position is *"I don't know if it will result in noticeably better perf, but you can try it out"* (https://github.com/AvaloniaUI/Avalonia/discussions/8120#discussioncomment-2740487), and kekekeks rejected an `SKImage`-over-mutable-`SKPixmap` implementation as *"strictly invalid"* because the pixmap must not change while the `SKImage` lives (https://github.com/AvaloniaUI/Avalonia/pull/17717#issuecomment-2532422090) — which is exactly what a live preview would do. The supported fallback is the custom `Control` + `Render` override used by `RenderDemo`.
- **A6: "a focused `TextBox` eats Enter"** → only when `AcceptsReturn` is true. A single-line `TextBox` — which the type-ahead is — leaves Enter unhandled, and never marks Space handled either (the character arrives via `TextInput`) — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/TextBox.cs#L1395
- **A6: "a focused `Button` eats Space"** → true only while focused, but the doc understates Enter: `Button` handles `Key.Enter` **unconditionally**, and a button with `IsDefault="True"` registers a handler on the input root that fires on Enter even unfocused. Set `IsDefault` on nothing — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/Button.cs#L297
- **A6's warning names the wrong failure.** The real one is the reverse direction: marking **Space** `Handled` in the tunnel handler suppresses character input to the focused type-ahead (Win32 `_ignoreWmChar`, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Windows/Avalonia.Win32/WindowImpl.AppWndProc.cs#L983; macOS `-keyDown:` returns early on `handled`, https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/native/Avalonia.Native/src/OSX/AvnView.mm#L795), so card names with spaces become untypeable. Fix: bail out without setting `Handled` when focus is in a text-entry control. It reproduces on macOS, so it is testable in this stream's own dev environment. Separately, `KeyBindings` are invoked before the tunnel phase runs at all, so don't mix the two mechanisms.
- **A5: "a `Contains` filter is plenty at that size — no fuzzy-matching dependency"** → the dependency conclusion holds; the performance claim does not, given `AutoCompleteBox`'s defaults. `RefreshView()` is an uncapped linear scan over an eager `List<object>` copy of all 33k, with `MinimumPrefixLength = 1` and `MinimumPopulateDelay = TimeSpan.Zero` — one letter scans everything synchronously on the UI thread per keystroke, with no cap on results. `AsyncPopulator` is the only hook that permits capping — https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/AutoCompleteBox/AutoCompleteBox.cs#L1331 and https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Avalonia.Controls/AutoCompleteBox/AutoCompleteBox.Properties.cs. The *Fallbacks* entry for this is consequently not a fallback but the baseline configuration.
- **`Avalonia.Diagnostics` has no 12.x release** (it stops at 11.3.22): the project was deleted from the repo and DevTools is now out-of-process. `this.AttachDevTools()` → `this.AttachDeveloperTools()` from `AvaloniaUI.DiagnosticsSupport` (2.2.3), plus the `AvaloniaUI.DeveloperTools` dotnet tool — https://github.com/AvaloniaUI/Avalonia/pull/20332, https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
- **`Avalonia.Controls.DataGrid` is deprecated in 12.x** — *"deprecated and only receives bug fixes … we recommend using TableView"* (https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid/blob/12.1.2/readme.md). **The plan's choice still stands**, because `TableView` has no sorting by deliberate design (MrJul, https://github.com/AvaloniaUI/Avalonia/issues/21237: *"No sorting, grouping or filtering or column re-ordering"*) and A7 requires "sortable by column". Recorded so the deprecation isn't discovered mid-build and mistaken for a wrong pin.
- **Done-when: "All four `TileState` values are reachable on demand by configuring `StubCardIdentifier`'s distances"** → only `Included` (with and without `IsLowConfidence`) and `Unresolved` are distance-driven. `Excluded` and `ManuallySet` are produced solely by `ToggleExcluded()` / `SetManually()`, i.e. by user action.
- **Done-when: "CSV opens cleanly in Excel"** → not this stream's criterion. A7 itself assigns encoding, BOM and escaping to stream D, and stream A has no store implementation to write a file with (see below).
- **A8: "offline"** → not a state this app has. LoreFetch is offline by design and the only network access in the project is the Scryfall bulk pull in `LoreFetch.Lab`. The real startup failures are a missing or unreadable hash index / thresholds file.
- **A2: `FrameGeometry`'s `Width`/`Height` vs `RotationDegrees` is ambiguous**, and the overlay is what breaks if it's read the wrong way — recorded under *Proposed contract changes* since the fix is a contract clarification.

### Proposed contract changes

- **The five fakes → seven: add `StubCollectionStore : ICollectionStore` and at least one `StubCollectionExporter : ICollectionExporter`** (ideally two, one with `IsVerified = false`, and a `DisplayName` containing a comma or accent). **Why:** A7 builds a `DataGrid` over `ICollectionStore.ListAsync()` and an export picker over the registered exporters, and A8 renders the empty-collection state — but `Core/Collection` and `Core/Export` belong to stream D and this stream must not touch them, and no fake covers either interface. As written, `CONTRACTS.md`'s promise that "stream A never needs anything real from B, C or D — not at the start, not at the end" is false for A7, A8 and one done-when criterion, and nothing can be added after the fork. This is the single highest-priority item in this review.
  **Effect on other streams:** unknown — for reconciliation.
- **`StubOracleCatalog`: make the entry count configurable (or seed ~33k synthetic entries alongside the hostile handful).** **Why:** A5's type-ahead is specified against ~33k names and `AutoCompleteBox`'s defaults are actively hostile at that size, but the stub holds "a few hundred" — so the stream cannot reproduce, or regression-test, the only performance problem A5 has. The hostile-name entries must stay; this is additive.
  **Effect on other streams:** unknown — for reconciliation.
- **`ScanSettings`: add `bool AutoCaptureEnabled { get; set; } = false;`** **Why:** the interaction model has an auto mode (`CLAUDE.md`: Space "ignores the expected count *even in auto mode*"), and A3 owns the settings surface, but there is no flag anywhere to turn auto-capture off — so as frozen, auto mode is always on and the UI cannot expose the toggle. Also the safer default for a first run.
  **Effect on other streams:** unknown — for reconciliation.
- **`ScanSettings`: add `int MovementTolerancePixels { get; set; } = 4;`** **Why:** A0's settle condition resets on "movement beyond ε" and ε has nowhere to live. The trigger *can* default it internally, but `SettleMilliseconds` already sits in `ScanSettings` and ε is the same kind of value — tunable per camera and mount. It cannot be added after the freeze. Proposed value: 4 px ≈ 0.03″ at the settled ~9.75″ height (≈139 px/inch), above contour jitter and far below hand movement.
  **Effect on other streams:** unknown — for reconciliation.
- **`IScanPipeline`: state `Capture()`'s thread-safety, and the thread `AutoCaptured` is raised on.** **Why:** the UI calls `Capture()` from a key handler on the UI thread while the pipeline thread is replacing and disposing the exact frame `Capture()` reads ("holds exactly one frame … and disposes it when the next one replaces it"). Nothing says that is safe, so as specified Space can rectify a buffer already returned to the pool — the same class of heisenbug the `byte[]`-over-`IntPtr` decision was made to avoid, one layer up. Relatedly, `Capture()` is synchronous and does up to nine rectify-and-identify passes inline, which on the UI thread is a visible freeze on every Space; the natural fix (run it off the UI thread) depends on the same answer. `FrameProcessed` documents its thread; `AutoCaptured` does not.
  **Effect on other streams:** unknown — for reconciliation.
- **`CONTRACTS.md`: publish how `IScanPipeline` is constructed, and who owns composition.** **Why:** `LoreFetch.App/**` is this stream's, so this stream writes the app's startup — which must construct the frame source, detector, rectifier, identifier, trigger, `ScanSettings` and the pipeline, and call `RunAsync`. The contract gives the pipeline's *interface* but no constructor or factory, doesn't say who calls `RunAsync`, and doesn't say who loads the thresholds file into `ScanSettings`. A1 cannot start without it. This also resolves the status line cleanly: if the App composes, it holds the `IFrameSource` and can read `Description` once at construction — which the doc's "the UI never reads an `IFrameSource`" rule appears to forbid, so either allow that narrow read explicitly or add `string SourceDescription { get; }` to `IScanPipeline`.
  **Effect on other streams:** unknown — for reconciliation.
- **`FrameGeometry`: state whether `Width`/`Height` are pre- or post-rotation.** **Why:** `CONTRACTS.md` says rotation lives in the source and downstream sees an upright frame, yet `FrameGeometry` carries `Width`, `Height` *and* `RotationDegrees`. A2 maps quads from frame coordinates to control coordinates; if those dimensions are the pre-rotation sensor dimensions, then at the settled 90° rotation every overlay quad is mapped through a transposed frame and lands nowhere near its card. Recommended: `Width`/`Height` are post-rotation and always match `CameraFrame.Width`/`Height`, with `RotationDegrees` informational.
  **Effect on other streams:** unknown — for reconciliation.
- **Package set: pin `Avalonia.Headless.XUnit` 12.1.2, and decide on `AvaloniaUI.DiagnosticsSupport` 2.2.3.** **Why:** every `.csproj` freezes before the fork, so a package not pinned now cannot be added. `Avalonia.Headless.XUnit` is the only way to test A6's keyboard map automatically, and it is not in this stream's package list. `Avalonia.Diagnostics` has no 12.x, so DevTools needs the new package or is simply absent (see *Open questions*). Per `CLAUDE.md`, over-reference rather than under-reference — an unused `PackageReference` costs nothing.
  **Effect on other streams:** unknown — for reconciliation.

### Open questions

- **Is the Avalonia 12 DevTools replacement actually free?** The breaking-changes doc says *"The Dev Tools included with **Avalonia Plus or higher** should be used instead"* and the install page references portal credentials and Plus/Pro/Enterprise tiers (https://docs.avaloniaui.net/tools/developer-tools/installation), while the archived repo's readme says it *"includes a free Community edition that covers all features available in the legacy `Avalonia.Diagnostics` package — no license required"* (https://github.com/AvaloniaUI/Avalonia.Diagnostics). Primary sources conflict. **Recommendation:** pin `AvaloniaUI.DiagnosticsSupport` in Stream 0 and resolve it in the first hour of stream A rather than at 2 a.m.; if it turns out to be paid, accept no DevTools — nothing is redistributed, so this is a dev-time convenience only and not a licence obligation for a GPLv3 project.
- **How does `CohortTile.Clear()` get a threshold?** Its contract is to revert a manual choice to "Included if the best candidate is within the ok threshold, otherwise Unresolved" — so the tile must compare a distance to `OkDistance`, which contradicts "the scan pipeline … is the only place thresholds are applied" and "nothing else in the codebase compares a distance to a threshold". A5's *Clear* behaviour and the matching done-when criterion depend on the answer. **Recommendation:** have the pipeline pass both thresholds into each `CohortTile` at construction, so the tile re-applies the pipeline's numbers rather than owning any — and reword the "one place" claim to "thresholds originate only in the pipeline".
- **Who loads the thresholds file into `ScanSettings` at startup?** `CONTRACTS.md` says stream B commits it next to the hash index and that it is "loaded into `ScanSettings` at startup", but startup code lives in `LoreFetch.App`, which is this stream's. **Recommendation:** assign the loader to Stream 0 next to the pipeline, so stream A neither implements nor owns a file format defined by stream B; stream A then only renders the failure when it's missing (A8).
- **Should the pipeline supply a monotonic `now` to the trigger?** `IAutoCaptureTrigger.Evaluate` takes a `DateTimeOffset`, which is wall-clock and can step backwards on an NTP correction or DST change. A backwards step stalls a 500 ms settle; a forward jump can satisfy it instantly. **Recommendation:** the pipeline derives `now` from a `Stopwatch` baseline rather than `DateTimeOffset.UtcNow`. No signature change, an implementation note for whoever writes the pipeline — but worth deciding before the trigger's unit tests bake in an assumption.
- **`net8.0` or `net10.0`?** Avalonia 12 dropped everything below `net8.0`, so the App's TFM is a Stream 0 choice affecting every project. **Recommendation:** `net8.0` unless another stream's dependency requires `net10.0` — it is the wider-compatibility option for a single-file `win-x64` publish, and nothing in this stream needs `net10.0`.
