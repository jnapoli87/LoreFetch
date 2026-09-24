# Decisions

The settled technical decisions for LoreFetch, each with the reasoning and the alternatives that were rejected. [`CLAUDE.md`](../CLAUDE.md) carries the hard rules as one-liners and points here for the why.

**To change a decision**, change this file in the same PR as the code, and say why in the PR description. A decision that no longer holds is worth rewriting, not working around.

Code comments and older docs cite these sections as `DECISIONS.md "<section>"`. Records written before the split (`RECONCILIATION.md`, `accuracy.md`, the design docs' review sections) cite them as `CLAUDE.md "<section>"`, and mean this file.

---

## Identification — ported perceptual hash, not OCR, not ML

The engine is a C# port of **CardSpotter**'s 1024-bit perceptual hash (`github.com/relgin/cardspotter`, **BSD-3-Clause**, GPLv3-compatible, must be attributed in `THIRD-PARTY-NOTICES`). Wizards' own SpellTable uses image hashing, not a neural net, despite the "card recognition AI" marketing — verified by inspecting its production WASM bundle — but whether it specifically ships CardSpotter's implementation is unverified, with no independent corroboration found.

Seven steps. **The two sides are deliberately asymmetric** — steps 2 and 3 run on the **reference side only**; the query side enters at step 4 with an already-rectified 488×680 card. Upstream does exactly this, and the convergence argument below *depends* on it: blurring and downsampling the reference is what destroys detail a webcam cannot reproduce. Steps 4–6 are shared and must be bit-identical.

| Step | | Side |
|---|---|---|
| 1 | Rectify the card (perspective-correct from the detected quad) — pinned `INTER_LINEAR`; `warpPerspective` does **not** support `INTER_AREA` | query |
| 2 | `GaussianBlur` 3×3, σ=1 (bit-exact across platforms — passing σ explicitly forces OpenCV's fixed-point path) | **reference only** |
| 3 | Resize to **96 px wide**, `INTER_AREA`, grayscale | **reference only** |
| 4 | Take region `width × 0.85·width` — the **top ~61%** of the card: title, art *and* type line | both |
| 5 | Resize that region to **32×32**, `INTER_AREA` | both |
| 6 | 4×4 grid of 8×8 cells; each bit = pixel > **median of its own cell** → **1024 bits** | both |
| 7 | Match by **full** 1024-bit Hamming distance | — |

**Step 7: do not port upstream's early rejection.** It is threshold-keyed and inadmissible — it prunes cards beyond a distance threshold rather than cards that cannot make the top N, so it changes the ranking. It also directly contradicts `ICardIdentifier`'s "NEVER filters by threshold". It buys nothing: brute force over 55k × 1024 bits measures **0.243 ms** per query, 2.19 ms for a 9-card cohort.

**Step 3 is `INTER_AREA` by choice, not by fidelity.** Upstream passes `cv::INTER_AREA` as `resize`'s 4th positional argument, which is `double fx` — so it silently runs `INTER_LINEAR`. That is an upstream bug. We build our own index, so what matters is that our two sides agree, not that they match CardSpotter's binaries; `INTER_AREA` is the correct filter for a ~5× downscale. A port that "faithfully" copies the call gets `INTER_LINEAR` by accident.

Why this survives webcam frames when naive pHash doesn't: rectification removes distortion rather than tolerating it; blurring and downsampling the *reference* side destroys the high-frequency detail a webcam can't reproduce, so photo and render converge; the **local median per cell** makes every bit invariant to local brightness, exposure and white balance; and the 4×4 grid lets glare corrupt some cells instead of failing the whole match.

**Rejected — do not re-propose:**

| Rejected | Why |
|---|---|
| OCR of the card name | Nothing in this problem space reads text, including SpellTable. The name is ~5 px tall at overhead height. Was the original plan's entire spine. |
| Embeddings (DINOv2/CLIP/ONNX) | FORB benchmark (NeurIPS 2023) ranks DINOv2 **worst of four** on trading-card retrieval, ~20.5 pts below CLIP on overall mAP@5 (89.36 vs 68.86). FORB evaluates no perceptual-hash baseline, so this is only a ceiling among embedding methods, not a comparison against our approach. Wants ≥224 px input and degrades at low resolution — fights our constraint where the hash works with it. |
| `Windows.Media.Ocr` | Officially "only supported for desktop apps with **package identity**" → requires MSIX, killing zip distribution. |
| Windows AI `TextRecognizer` | Runs "exclusively on devices with an NPU" — Copilot+ PCs only. |
| Emgu.CV | Free tier is **GPLv3-only**; OpenCvSharp is Apache-2.0 and better maintained. |
| Tesseract | The one project that benchmarked it calls it "pretty bad" on MTG cards. |

OCR survives as the **v2** path for one thing only: printing/set disambiguation, which needs the collector line and a low mount.

## Card detection — freehand, no registration jig

Cards are placed by hand. There is **no printed tray**, so fixed ROIs will not register. Detect per frame:

`Canny` → `findContours` `RETR_EXTERNAL` → filter on aspect **1 : 1.397** (63/88 mm) ±15%, widening to ±25% if detection misses, **and** minimum area → take top N by area → `getPerspectiveTransform` + `warpPerspective` to canonical size.

Log the discard reason for every rejected contour. Detection will find hands, sleeves and mat seams — **better to detect nothing than to hash a hand.**

## Geometry — one height serves every layout

The camera stays **landscape and unrotated** — `ScanSettings.CameraRotationDegrees` is **0** at composition time (the `ScanSettings` default itself stays 90°; this is a value set at composition, not a change to the frozen contract). C920 is 78° diagonal → HFOV 70.4°, VFOV 43.3° → **px/inch = 1360 / height_inches**.

| Layout | Footprint (across × deep) | Geometric floor |
|---|---|---|
| 1 card | 3.5″ × 2.5″ | 3.1″ |
| 3 in a line | 10.7″ × 2.5″ | 7.6″ |
| **3×3 grid, cards rotated** | 10.7″ × 7.7″ | **9.75″** ← binding |

Each floor assumes the layout's long side runs across the frame — the 1920 axis — which is the arrangement the project uses throughout.

That **9.75″ is a geometric floor, not an operating height**: a 3×3 laid out with its cards rotated first fits the landscape frame there with just **0.04″ of margin** — one millimetre, so a card leaves the frame if the mat shifts. The **operating height is 12″** (1.83″ of margin), and the accuracy sweep runs **12″ and 20″**.

A 3×3 of portrait cards is far taller than wide while the sensor is 16:9, so laid out that way it costs 38% linear resolution in the wrong orientation — needing 13.5″ and yielding only 251×350 px. **The fix is to rotate the layout, not the camera**: the 3×3 grid is laid out with its cards **rotated**, long edge across the frame, giving it a 10.7″ × 7.7″ footprint that fits the landscape frame at 12″ with 1.83″ to spare, at **identical pixel resolution** — 283×397 px either way, since px/inch is the same on both axes. Rotating the layout substitutes for rotating the camera, so the camera mount stays landscape and unrotated for every layout; **the mount locks once** at 12″ (sweep also covers 20″), and 1/3/9 differ purely in software. The 3D printer's job is the adjustable camera mount (stand or gantry bracket) — nothing else.

**Derive each fixture's height from its frame, not from a tape measure:** `height_inches = 1360 × 2.5 / card_pixel_width`. This runs through the real optics rather than trusting a label. It matters because a mislabelled height does not fail loudly — it reads as mysteriously poor accuracy in the per-height accuracy table. (Evidence: a batch of ad-hoc frames recorded as shot at "~12″" had a ~260×370 px card, which implies 13.1″ from the width and 12.9″ from the height — both axes agree the tape was an inch out, not the `1360` constant.)

## Interaction — capture, then accept the cohort

| Key / action | Effect |
|---|---|
| **Space** | Capture **whatever is detected now** — ignores the expected count even in auto mode, so 2 cards down means a 2-card cohort. Replaces any pending cohort. No-op on 0 detections. |
| **Enter** | Accept the cohort — commit every non-X'd tile, clear, re-arm. |
| **Escape** | Discard the cohort; DB untouched. |
| **Left-click tile** | Toggle an **X** (opt-out). No X = included. |
| **Right-click tile** | *Set card manually…* (type-ahead over the oracle catalog) / *Clear* — reverts a manual choice to the hash's own proposal. |

Happy path is **space, enter, space, enter** — no mouse. Auto mode fires on a **count-gated settle**: exact expected count (1/3/9) held stable for ≥500 ms, resetting on any count change or movement, and **requiring the scene to break before re-arming** — without that re-arm rule a static tableau re-fires forever. Hash-set dedupe sits behind it.

Capture only *fills the grid*; commit is always the separate explicit Enter. That separation is what makes auto mode safe — a spurious capture costs an Escape, not a corrupted row.

Match distance drives **emphasis, not gating**: low-confidence tiles are highlighted so the eye lands there. Enter is the real gate, per-cohort rather than per-card, which is what preserves throughput.

## Storage — CSV, not a database

v1 stores one flat table of card rows (full column list below). Nothing relational is happening, so there is no database. The old plan specified EF Core + SQLite; that was inherited and never re-justified against this data model.

Why CSV wins here:
- **The storage format *is* the export format**, so there is no export code path and no class of bug where storage and export disagree. The whole deliverable is "export your collection."
- **The file stays readable and repairable by ordinary tools** — a consequence of the format, not a goal anyone asked for. The remedy for a wrong match is the opt-out grid at capture time, and after the fact the `BestMatchDistance` + `Source` query; *not* expecting users to hand-edit CSV. What this does buy is that the store must survive reading back a file it did not write byte-for-byte, which is a robustness requirement on the reader rather than a feature.
- Zero dependencies, no EF ceremony, and integration tests are write-file / read-file / assert.

Accepted costs: full-file rewrite per commit (~300 KB at 10k rows, microseconds), no indexing (linear scan of 10k rows is nothing), no transactions (covered by write-temp-then-rename — documented atomic on APFS, undocumented on NTFS, and only ever a guarantee that a reader sees no *truncated* file, never that the write survived power loss; the `.bak` covers the rest). UTF-8 **with BOM** or Excel mangles non-ASCII card names.

**v1 ships two formats: the native SOT and one third-party adapter, Moxfield** — the only researched tool that provably accepts name-only rows. ManaBox requires a set or Scryfall id and so *cannot* take our rows; Archidekt blocks name-only uploads; Deckbox works but is documented only by folklore; Dragon Shield has no import documentation at all. One verified adapter beats four guessed ones; the README says which and why. Details in `docs/CONTRACTS.md`.

**The native format is the source of truth, and every export adapter projects *down* from it** — so it carries everything available at commit time, not just what v1's adapters consume: `OracleId` (the Scryfall stable key, free from the hash index), `OracleName`, `Quantity`, `Condition`, `LastScannedAt`, `BestMatchDistance`, `Source` (`Hash` | `Manual`). A three-column SOT would permanently cap what any future exporter could emit.

`BestMatchDistance` + `Source` together make *"show me everything the machine set at distance > 200"* answerable — and since identification is opt-out, that query is the remedy for wrong matches that slipped past the grid.

Dedup identity is **`OracleId` + condition** → increment quantity; the name is display only, because keying on it breaks the first time Scryfall renames a card. **Condition is blank in v1** — the camera can't grade a card and nothing in the scan loop asks, and a column that always says "NM" would be false precision. Blank is a value like any other for dedup. The header row's exact column set is the format version; a reader seeing unknown or missing columns must **fail loudly rather than mis-parse**. Keep a `.bak` of the previous write.

**Revisit when v2 adds printings, prices or scan history** — at that point SQLite earns its place. `ICollectionStore` is the seam, so that swap stays contained. That containment is the argument for starting simple, not a reason to pre-build for v2.

## Scope

Modern frame (2015+), English, non-foil, single-faced. **Oracle name only — no printing/set resolution**, which the hash cannot do anyway since reprints share art. Consequently no price column (no printing → no unambiguous price).

**Camera hardware is out of scope.** LoreFetch needs a webcam looking straight down from a known height; how it is held there (a gantry, duct tape) is the user's business, not this repo's. The 3D-printed mount in `docs/history/` was a hackathon rig item and is not planned.

Ladder: **basic lands → normal cards → everything else is stretch.**

⚠️ **Lands are a smoke test, not a benchmark.** Every Forest art collapses to "Forest", so retrieving the *wrong* Forest still scores correct@1 — accuracy on lands is inflated by a category that structurally cannot fail. Full-art lands also lack a type line where the hash region expects one. **Measure accuracy on normal cards only.**

## Stack, with the traps

| Layer | Pin | Trap |
|---|---|---|
| UI | **Avalonia 12.1.2** | Chosen because it's the only option that both builds *and runs* on macOS. `Avalonia.Controls.DataGrid` is a separate NuGet. Stay on 12.1.x — 12.0.x has a 5–40× render-pass regression. `Avalonia.Headless.XUnit` 12.1.2 requires **xunit v3**, so every test project uses v3. |
| CV | **OpenCvSharp4 `4.13.0.20260627`** + `runtime.win` + `runtime.osx.arm64` | **No 4.12.x exists** (4.11 → 4.13). **Never use `.slim`** — it disables `videoio`, i.e. no camera. Versions below `4.13.0.20260602` break single-file publish (used `Assembly.Location`, empty under single-file). |
| Capture | **FlashCap 1.12.0** (Apache-2.0) | `EnumerateDescriptors()` does explicit format negotiation — this is why it's here. |
| Frame → screen | **One reused `WriteableBitmap`**, filled from the `byte[]` frame | Do **not** use `OpenCvSharp5.AvaloniaExtensions` — it depends on **`OpenCvSharp5`**, a second OpenCvSharp binding beside our `OpenCvSharp4`, and on Avalonia 12.1.1; frames cross the seam as `byte[]`, so it has nothing to do. Nor `OpenCvSharp4.Extensions` (GDI+ / `System.Drawing.Common`, throws off-Windows) or `.WpfExtensions`. |
| Store | **CSV is the store of record** — no database | Temp file **in the target's own directory** (`%TEMP%` silently degrades the rename to copy+delete), `Flush(true)`, then `File.Move(overwrite: true)`. Documented atomic on APFS, undocumented either way on NTFS. Full rewrite per commit is ~300 KB at 10k rows. |

**The C920 trap.** It is **USB 2.0**, so at 1920×1080 the uncompressed YUYV mode can only advertise **5 fps** — bus bandwidth, not slow software. MJPG advertises 30. OpenCV's `set()` for FOURCC **returns false and silently leaves you on YUY2**, which is the origin of every "my C920 is 5 fps" report. So: prefer `DSHOW` (1.44 s to first frame vs MSMF's 5.71 s at 1080p); set `FourCC = "MJPG"` **before** width/height; **read the properties back and assert**. If MSMF is unavoidable, set `OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS=0` *before the first OpenCvSharp type is touched*. Unmeasured cost to watch: MJPEG means 30 × 1080p JPEG decodes/sec on the CPU.

**Avalonia preview.** Allocate **one** `WriteableBitmap` and mutate it — per-frame allocation is the documented cause of every "camera preview is choppy" report. Convert BGR→BGRA before blitting, respect `RowBytes` (Skia pads rows), and call `InvalidateVisual()` rather than rebinding `Source`. Draw overlays as vector children over the `Image`, not baked into the Mat. **And dispose the `Lock()` every frame** — Skia caches an `SKImage` snapshot that only `BitmapFramebuffer.Dispose()` invalidates, so a held lock renders frame 1 forever. (An earlier "measured ceiling is 60 fps @ 1080p" claim here had no primary source — the person cited is a contributor, not a maintainer, and said he had not benchmarked it. Removed rather than restated; the approach stands on the allocation argument alone.)

**Publish.** `-r win-x64 --self-contained -p:PublishSingleFile=true` **plus `-p:IncludeNativeLibrariesForSelfExtract=true`** — without that flag `OpenCvSharpExtern.dll` ships as a loose file and it isn't single-file. Expect ~150–250 MB. Unsigned exe trips SmartScreen; note it in the README.

## Scryfall

**No API key and no authentication exist** — nothing to register for. Requirements:

- Both `User-Agent` (`LoreFetch/0.1 (github.com/jnapoli87/LoreFetch)`) **and** `Accept` headers are mandatory. Their docs say explicitly: do not let an HTTP library choose the User-Agent.
- Rate limits are IP-based: 2/sec on `/cards/*`, 10/sec elsewhere; a 429 locks you out 30 s and "it is not acceptable to ignore HTTP 429 responses." **`*.scryfall.io` file origins are unmetered**, so the image pull for the hash build is bounded by wall clock and disk, not rate limit. **Pull `normal` (488×680), not `small`** — `small` is 146×204, and building the index from it means the reference side's 96 px step is a 1.5× downscale while the query side's is 5.1×, so the two sides can never converge and the distance floor is permanently and invisibly inflated. `normal` is *exactly* `RectifiedCard`'s canonical size, which is the whole reason 488×680 was chosen. Cost: **~6.0 GB** (109.6 KB average) rather than ~0.79 GB. Fallback if that is impractical is `border_crop` (480×680) — never `small`.
- Bulk data is **gzipped JSONL**: field `jsonl_download_uri`, size field `compressed_size`. The old `download_uri`/`size` fields are gone and most tutorials are stale. Resolve via `GET /bulk-data/{type}`; filenames carry a daily timestamp, so never hardcode. Use **`unique_artwork`**.
- Counts, re-measured 2026-09-21 against the live bulk file: **54,963** objects in `unique_artwork` over **37,926** oracle ids; **48,713** arts over **33,578** oracle ids after a scope-appropriate filter. The older 32,992 / 53,482 figures were drift, not error. **3,440 objects have no top-level `image_uris`** (multi-faced — images live per face) and will crash or silently vanish in a naive builder; skip them explicitly, along with `art_series` and `token` layouts.
- Display terms forbid distorting/blurring/cropping-off the copyright line on images *shown to users*, and require artist credit alongside an art crop. Internal transforms for hashing are fine.

**Never commit card imagery** — not Scryfall renders, and not your own photographs either; the artwork is WotC IP regardless of who shot it. The fixture corpus stays local and gitignored. Commit only *derived* data: the hash index (**6.0 MiB** of hash payload in scope, ~8.2 MiB once the name table is counted — the old "~8.6 MB from ~67k arts" figure was arithmetic on a count that was never real) and the accuracy tables. CI therefore runs against synthetically generated frames. README carries a **WotC Fan Content Policy disclaimer**.

## Platform

Ship **`win-x64` binary only**. But the codebase stays portable — no project named `.Windows`, no gratuitous `#if WINDOWS`, and the `macos-latest` CI leg exists to enforce that. Everything already runs on Apple Silicon, mostly as a side effect of dropping OCR. FlashCap **does** have an AVFoundation backend as of 1.11.0 — the earlier claim that none existed was stale — but issue #182 reports it crashing natively and delivering mis-channelled colour, so the decision is unchanged and better supported than before: no device-opening test on the `macos-latest` leg. macOS is documented as "should work, unsupported, build from source"; supporting it properly would mean `Info.plist` camera entitlements, Gatekeeper instructions, a `.app` bundle and Mac-camera testing — which is exactly the trap the original plan fell into by building *for* two platforms.

## Git identity

**Commit identity is each contributor's own.** `scripts/lorefetch.sh setup` wires only `core.hooksPath = hooks`, repo-locally, and touches nothing global. Until #5 it also pinned the owner's name, noreply address and SSH key, and the hook refused every other identity, so nobody else could commit, and a contributor who followed `setup` committed *as the owner*.

The owner's global default is a work address, so the owner's checkouts carry repo-local `user.email = jnapoli87@users.noreply.github.com` and a `core.sshCommand` pinning `~/.ssh/id_ed25519_personal` (GitHub won't accept one key on two accounts). A fresh clone needs both set by hand; nothing in the repo enforces them any more.

`hooks/pre-commit` enforces **two hard rules, no card imagery and CardSpotter attribution**, and CI runs the same file as the required `guards` check. The hook is tracked, so it survives a fresh clone (where `.git/hooks/` would not) and applies inside every worktree.

**What the hook checks, and why each detail matters:**

| Check | Detail |
|---|---|
| Imagery | Rejects raster files in the index (`git ls-files`) outside `src/LoreFetch.App/Assets/` and `docs/img/`. This is the layer that catches **`git add -f`**, which bypasses `.gitignore` entirely. It reads the index, not the staged diff, because in CI nothing is staged: the index is the commit under test. |
| Attribution | Requires `THIRD-PARTY-NOTICES` to retain the CardSpotter **credit, copyright holder, BSD-3 conditions *and* disclaimer** — all four. BSD-3-Clause requires "this list of conditions" be retained, so a bare "uses CardSpotter" credit does not satisfy it, and trimming the file to one is the realistic way this rots. Checked against the working tree, not the index: the obligation is about what the repo *contains*, not what this commit touched. |
| Attribution | Also prints an **advisory** (non-blocking) list of `PackageReference` ids absent from the notices file, since packages legitimately land before their notice entry does. |

**Why both a git hook and CI:** the hook catches a violation before the push, from any tool. But a hook can be left unwired or skipped with `--no-verify`, so only a required check guarantees `main`. CI runs the hook file itself rather than a copy, so the two cannot disagree.

⚠️ **Write optional regex groups as `(x)?`, never `(|x)`.** BSD grep on macOS rejects an empty alternative with *"empty (sub)expression"* and then matches nothing, which once silently turned an earlier version of this hook into "refuse every commit". It only surfaced because the hook was tested rather than eyeballed. **An untested guard is not a guard.**

`gh` CLI auth is per-host, not per-repo — it is machine-wide, not bound to this repo. On the Windows PC it is logged in as `jnapoli87` (verified 2026-09-24), so `gh` is fine for this repo there. On any other machine, run `gh auth status` before a `gh` write; publishing via `gh` is still subject to the review-before-push rule.

## The one gate that matters most

**A Scryfall render must retrieve its own artwork through the full query path, at a small, recorded, stable Hamming distance.** Not ≈ 0 — the reference side blurs and downsamples where the query side does not, so a floor is *expected*, and demanding zero would mean deleting the mechanism that makes a webcam frame match a print render.

The invariant is therefore not "both sides identical" but: **each side's transform exists exactly once in the codebase, the shared steps are bit-identical, and neither side changes without rebuilding the index.** Guard it with committed golden hashes (Windows leg only — see below) plus a round-trip test that asserts the measured floor as a bound, before building anything on top.

Two ways this still fails silently, both guarded rather than assumed:

- **Artwork granularity.** `unique_artwork` holds 54,963 arts over 37,926 oracle ids, so a gate asserting only `OracleId` passes while matching a *different art of the same card*. Assert on `ArtworkId`.
- **Architecture.** `INTER_AREA` is **not** bit-exact across x86-64 and ARM64 (carotene's NEON HAL; OpenCV #24163 confirmed, #22477 closed won't-fix). So **the index is built and committed on `win-x64`**, the ship target, and **the golden-hash test is pinned to the same architecture: `[Trait("Category","WindowsOnly")]`, filtered out on the `macos-latest` leg.** Running one set of goldens on both legs was the earlier call and it was wrong — since `macos-latest` is ARM64, a shared golden cannot pass there, so the leg would be permanently red and the guard would be turned off rather than obeyed. The thing worth guarding is an *unintended* change to either transform, and one architecture proves that. What the macOS leg still proves, which is its actual job, is that `Core` carries no Windows-only dependency.

## Real risks, in order

1. **Glare, focus and tilt — not resolution.** These are the top four items in Wizards' own SpellTable troubleshooting list, and they destroy the local-median bit pattern. Mitigation is physical: diffuse off-axis lighting (a SAD lamp works; position it at a shallow angle, not beside the camera, and check for PWM flicker banding against the 30 fps shutter). Colour temperature is irrelevant — we grayscale before hashing.
2. **Reference/query transform divergence.** Not "the two sides differ" — they differ *by design*. The risk is an **unintended** change to either side, or to the shared steps, after the index is built. Guarded by golden hashes, one transform function per side, and a rebuild-on-change rule. See the gate above.
   **New, found in review: `INTER_AREA` is not bit-exact across x86-64 and ARM64.** An index built on the Mac may not match queries hashed on Windows, presenting exactly as "degrades silently". Mitigated by building the index on `win-x64` and pinning the golden-hash test to `win-x64` too, traited `WindowsOnly` so the macOS leg filters it out instead of going permanently red on a divergence it cannot avoid.
3. **False card detections** — filter on aspect *and* area, log every rejection.
4. **Mat contrast.** Detection depends on finding the card's edge, and modern cards are black-bordered, so a *dark* mat is the worst case — which is what the original plan recommended four times. Settle it by measurement.
5. **Same-art printings are permanently indistinguishable** by hash. Accepted (oracle name only), but say so in the README rather than letting users discover it.
6. **Foils.** Glare defeats hashing without polarized or diffuse light. Out of v1 scope — document the failure rather than hiding it.
7. **`OpenCvSharp4.runtime.osx.arm64` has exactly one release** (2026-06-27, ~6k downloads). No bug reports, which may mean "works" or "unused". Dev-only. Don't confuse it with `OpenCvSharp4.runtime.osx_arm64` (underscore), an unofficial package.

## Card imagery is enforced in three layers, not just documented

The artwork is Wizards of the Coast IP regardless of who photographed it, so neither Scryfall renders nor our own captures may be committed. Once artwork is in history it is there permanently, short of a rewrite — so this is enforced three times:

1. **`.gitignore`** blocks all raster formats tree-wide, opting UI/doc assets back in individually, so a stray fixture can't slip through on `git add -A`. Verify with `git check-ignore -v <path>`.
2. **`hooks/pre-commit`** rejects raster files outside the allowed asset paths — which catches `git add -f`, the one move that bypasses `.gitignore` completely.
3. **The `guards` CI check** runs that same hook on every PR, so an unwired or skipped hook cannot let imagery reach `main`. It cannot keep an image off the PR branch itself, which is already pushed by then; that is the hook's job.

Only **derived** data is committed: the ~8.2 MiB hash index and the accuracy tables. The raw Scryfall downloads that build the index are ignored, as is the user's own `collection.csv`.

## Repository guards

`main`'s ruleset on GitHub blocks deletion, force-pushes and direct pushes, merges only by squash through a PR, and requires every CI check (both build legs, `lint`, `guards`, `contract-check`, `metrics`) on a branch that is up to date with `main`. It has no bypass actors.

It replaced `.claude/hooks/guard-bash.sh` (#5), a Claude Code hook that denied force-pushes and commits from an unwired checkout and warned before `git push` and `gh pr create`. A server rule binds every client, where a Claude hook bound only Claude. The review-before-publish rule in `CLAUDE.md` still stands; no hook re-raises it any more.

## Two things that bite a fresh clone

**1. A fresh clone's hook is unwired, and nothing about it looks wrong.** `hooks/pre-commit` is tracked, so the file arrives, but **`core.hooksPath` is config, and config does not clone.** `scripts/lorefetch.sh setup` wires it, repo-locally, and **`doctor` exits non-zero** until it is, so it is a check rather than a report. CI's `guards` check runs the same file, so an unwired hook no longer lets anything reach `main`; it only moves the failure to after the push. In the owner's clones, `user.email` also falls back to the global work address until it is set by hand (see *Git identity*).

**2. History was rewritten on 2026-09-21, so every commit hash changed.** A clone predating that must `git fetch && git reset --hard origin/main`. **Not `git pull`** — a merge would drag the old history back in and undo the rewrite. Prefer the reset over re-cloning, because of point 1. The rewrite was verified by cloning from GitHub afterwards and grepping every commit's content, messages and trees.
