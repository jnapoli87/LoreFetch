# LoreFetch

Turn any webcam into a Magic: The Gathering collection scanner. Fully offline, no subscription, no phone.

Private personal project, **GPLv3**, unrelated to any employer or day job. Built in a 24-hour hackathon window.

**This file holds the settled decisions.** Sequencing lives elsewhere:

| Document | Job |
|---|---|
| [`docs/PLAN.md`](docs/PLAN.md) | The spine: Stream 0, the four streams, integration, endgame |
| [`docs/CONTRACTS.md`](docs/CONTRACTS.md) | The frozen seam that makes parallel streams possible |
| [`docs/stream-a-ui.md`](docs/stream-a-ui.md) | Avalonia app, built against fakes |
| [`docs/stream-b-identification.md`](docs/stream-b-identification.md) | Hash port, index, detection, accuracy ← *the risky one* |
| [`docs/stream-c-capture.md`](docs/stream-c-capture.md) | FlashCap → `IFrameSource` |
| [`docs/stream-d-export.md`](docs/stream-d-export.md) | Native SOT format + third-party adapters |
| [`docs/TESTING.md`](docs/TESTING.md) | Five test levels and what each must assert |

Work happens in **four parallel git worktrees** after a serial ~2h foundation pass. `Core/Abstractions` and every `.csproj` are **frozen** once the streams fork — if a stream needs a contract change it *stops and asks*, because every unilateral edit there is a four-way merge conflict.

⚠ **That freeze is mechanically enforced, which makes it a sequencing constraint.** `.claude/hooks/guard-write.sh` refuses those edits from inside a linked worktree, so a missing package reference or an incomplete contract **cannot be fixed from a stream** — fork with a gap and all four streams stall until someone returns to `main`. Finish and review the contracts and project files *before* creating any worktree, and over-reference packages rather than under-reference: an unused `PackageReference` costs nothing.

---

## Decisions that are settled

### Identification — ported perceptual hash, not OCR, not ML

The engine is a C# port of **CardSpotter**'s 1024-bit perceptual hash (`github.com/relgin/cardspotter`, **BSD-3-Clause**, GPLv3-compatible, must be attributed in `THIRD-PARTY-NOTICES`). This is the algorithm shipping in Wizards' own SpellTable — verified by inspecting its production WASM bundle, which contains no neural net despite the "card recognition AI" marketing.

Seven steps, in order. Reference side and query side **must** apply the identical transform:

1. Rectify the card (perspective-correct from the detected quad)
2. `GaussianBlur` 3×3, σ=1
3. Resize to **96 px wide**, `INTER_AREA`, grayscale
4. Take region `width × 0.85·width` — the **top ~61%** of the card: title, art *and* type line
5. Resize that region to **32×32**
6. 4×4 grid of 8×8 cells; each bit = pixel > **median of its own cell** → **1024 bits**
7. Match by Hamming distance, with per-cell grid distances and early rejection

Why this survives webcam frames when naive pHash doesn't: rectification removes distortion rather than tolerating it; blurring and downsampling the *reference* side destroys the high-frequency detail a webcam can't reproduce, so photo and render converge; the **local median per cell** makes every bit invariant to local brightness, exposure and white balance; and the 4×4 grid lets glare corrupt some cells instead of failing the whole match.

**Rejected — do not re-propose:**

| Rejected | Why |
|---|---|
| OCR of the card name | Nothing in this problem space reads text, including SpellTable. The name is ~5 px tall at overhead height. Was the original plan's entire spine. |
| Embeddings (DINOv2/CLIP/ONNX) | FORB benchmark (NeurIPS 2023) ranks DINOv2 **worst of four** on trading-card retrieval, 24 pts below CLIP. Wants ≥224 px input and degrades at low resolution — fights our constraint where the hash works with it. |
| `Windows.Media.Ocr` | Officially "only supported for desktop apps with **package identity**" → requires MSIX, killing zip distribution. |
| Windows AI `TextRecognizer` | Runs "exclusively on devices with an NPU" — Copilot+ PCs only. |
| Emgu.CV | Free tier is **GPLv3-only**; OpenCvSharp is Apache-2.0 and better maintained. |
| Tesseract | The one project that benchmarked it calls it "pretty bad" on MTG cards. |

OCR survives as the **v2** path for one thing only: printing/set disambiguation, which needs the collector line and a low mount.

### Card detection — freehand, no registration jig

Cards are placed by hand. There is **no printed tray**, so fixed ROIs will not register. Detect per frame:

`Canny` → `findContours` `RETR_EXTERNAL` → filter on aspect **1 : 1.397** (63/88 mm) ±15%, widening to ±25% if detection misses, **and** minimum area → take top N by area → `getPerspectiveTransform` + `warpPerspective` to canonical size.

Log the discard reason for every rejected contour. Detection will find hands, sleeves and mat seams — **better to detect nothing than to hash a hand.**

### Geometry — one height serves every layout

Orient the camera so the **1920 axis runs along the table's depth**, and orient every layout's long side along that same axis. C920 is 78° diagonal → HFOV 70.4°, VFOV 43.3° → **px/inch = 1360 / height_inches**.

| Layout | Footprint | Min height |
|---|---|---|
| 1 card | 2.5″ × 3.5″ | 3.1″ |
| 3 in a line | 2.5″ × 10.7″ | 7.6″ |
| **3×3 grid** | 7.7″ × 10.7″ | **9.75″** ← binding |

At a fixed **~9.75″** every card in every mode is **346 × 483 px**, which is 1.6–2.7× SpellTable's proven 130–215 px working range. **The mount locks once**; 1/3/9 differ purely in software. The 3D printer's job is the adjustable camera mount (stand or gantry bracket) — nothing else.

Getting the axis wrong costs 38% linear resolution: a 3×3 of portrait cards is far taller than wide while the sensor is 16:9, so in the wrong orientation the vertical binds, it needs 13.5″, and yields only 251 × 350 px.

### Interaction — capture, then accept the cohort

| Key / action | Effect |
|---|---|
| **Space** | Capture **whatever is detected now** — ignores the expected count even in auto mode, so 2 cards down means a 2-card cohort. Replaces any pending cohort. No-op on 0 detections. |
| **Enter** | Accept the cohort — commit every non-X'd tile, clear, re-arm. |
| **Escape** | Discard the cohort; DB untouched. |
| **Left-click tile** | Toggle an **X** (opt-out). No X = included. |
| **Right-click tile** | *Set card manually…* (type-ahead over the oracle names in the hash DB) / *Clear*. |

Happy path is **space, enter, space, enter** — no mouse. Auto mode fires on a **count-gated settle**: exact expected count (1/3/9) held stable for ≥500 ms, resetting on any count change or movement, and **requiring the scene to break before re-arming** — without that re-arm rule a static tableau re-fires forever. Hash-set dedupe sits behind it.

Capture only *fills the grid*; commit is always the separate explicit Enter. That separation is what makes auto mode safe — a spurious capture costs an Escape, not a corrupted row.

Match distance drives **emphasis, not gating**: low-confidence tiles are highlighted so the eye lands there. Enter is the real gate, per-cohort rather than per-card, which is what preserves throughput.

### Storage — CSV, not a database

v1 stores **three columns**: oracle name, quantity, condition. Nothing relational is happening, so there is no database. The old plan specified EF Core + SQLite; that was inherited and never re-justified against this data model.

Why CSV wins here:
- **The storage format *is* the export format**, so there is no export code path and no class of bug where storage and export disagree. The whole deliverable is "export your collection."
- **Users can hand-fix a bad row in Excel.** This matters: identification is opt-out, so some wrong matches will slip through, and being able to correct the file is a feature.
- Zero dependencies, no EF ceremony, and integration tests are write-file / read-file / assert.

Accepted costs: full-file rewrite per commit (~300 KB at 10k rows, microseconds), no indexing (linear scan of 10k rows is nothing), no transactions (covered by write-temp-then-rename, atomic on NTFS and APFS). UTF-8 **with BOM** or Excel mangles non-ASCII card names.

**The native format is the source of truth, and every export adapter projects *down* from it** — so it carries everything available at commit time, not just what v1's adapters consume: `OracleId` (the Scryfall stable key, free from the hash index), `OracleName`, `Quantity`, `Condition`, `LastScannedAt`, `BestMatchDistance`, `Source` (`Hash` | `Manual`). A three-column SOT would permanently cap what any future exporter could emit.

`BestMatchDistance` + `Source` together make *"show me everything the machine set at distance > 200"* answerable — and since identification is opt-out, that query is the remedy for wrong matches that slipped past the grid.

Dedup identity is **oracle name + condition** → increment quantity. The header row's exact column set is the format version; a reader seeing unknown or missing columns must **fail loudly rather than mis-parse**. Keep a `.bak` of the previous write.

**Revisit when v2 adds printings, prices or scan history** — at that point SQLite earns its place. `ICollectionStore` is the seam, so that swap stays contained. That containment is the argument for starting simple, not a reason to pre-build for v2.

### Scope

Modern frame (2015+), English, non-foil, single-faced. **Oracle name only — no printing/set resolution**, which the hash cannot do anyway since reprints share art. Consequently no price column (no printing → no unambiguous price).

Ladder: **basic lands → normal cards → everything else is stretch.**

⚠️ **Lands are a smoke test, not a benchmark.** Every Forest art collapses to "Forest", so retrieving the *wrong* Forest still scores correct@1 — accuracy on lands is inflated by a category that structurally cannot fail. Full-art lands also lack a type line where the hash region expects one. **Measure accuracy on normal cards only.**

### Stack, with the traps

| Layer | Pin | Trap |
|---|---|---|
| UI | **Avalonia 12.1.2** | Chosen because it's the only option that both builds *and runs* on macOS. `Avalonia.Controls.DataGrid` is a separate NuGet. `OpenCvSharp5.AvaloniaExtensions` pins 12.1.1, so stay on 12.1.x. |
| CV | **OpenCvSharp4 `4.13.0.20260627`** + `runtime.win` + `runtime.osx.arm64` | **No 4.12.x exists** (4.11 → 4.13). **Never use `.slim`** — it disables `videoio`, i.e. no camera. Versions below `4.13.0.20260602` break single-file publish (used `Assembly.Location`, empty under single-file). |
| Capture | **FlashCap 1.12.0** (Apache-2.0) | `EnumerateDescriptors()` does explicit format negotiation — this is why it's here. |
| Mat → screen | `OpenCvSharp5.AvaloniaExtensions`, or one reused `WriteableBitmap` | Do **not** use `OpenCvSharp4.Extensions` (GDI+ / `System.Drawing.Common`, throws off-Windows) or `.WpfExtensions`. |
| Store | **CSV is the store of record** — no database | Write via temp-file + atomic rename (atomic on NTFS and APFS). Full rewrite per commit is ~300 KB at 10k rows. |

**The C920 trap.** It is **USB 2.0**, so at 1920×1080 the uncompressed YUYV mode can only advertise **5 fps** — bus bandwidth, not slow software. MJPG advertises 30. OpenCV's `set()` for FOURCC **returns false and silently leaves you on YUY2**, which is the origin of every "my C920 is 5 fps" report. So: prefer `DSHOW` (1.44 s to first frame vs MSMF's 5.71 s at 1080p); set `FourCC = "MJPG"` **before** width/height; **read the properties back and assert**. If MSMF is unavoidable, set `OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS=0` *before the first OpenCvSharp type is touched*. Unmeasured cost to watch: MJPEG means 30 × 1080p JPEG decodes/sec on the CPU.

**Avalonia preview.** Allocate **one** `WriteableBitmap` and mutate it — per-frame allocation is the documented cause of every "camera preview is choppy" report. Convert BGR→BGRA before blitting, respect `RowBytes` (Skia pads rows), and call `InvalidateVisual()` rather than rebinding `Source`. Measured ceiling is 60 fps @ 1080p, above our 30 fps need. Draw overlays as vector children over the `Image`, not baked into the Mat.

**Publish.** `-r win-x64 --self-contained -p:PublishSingleFile=true` **plus `-p:IncludeNativeLibrariesForSelfExtract=true`** — without that flag `OpenCvSharpExtern.dll` ships as a loose file and it isn't single-file. Expect ~150–250 MB. Unsigned exe trips SmartScreen; note it in the README.

### Scryfall

**No API key and no authentication exist** — nothing to register for. Requirements:

- Both `User-Agent` (`LoreFetch/0.1 (github.com/jnapoli87/LoreFetch)`) **and** `Accept` headers are mandatory. Their docs say explicitly: do not let an HTTP library choose the User-Agent.
- Rate limits are IP-based: 2/sec on `/cards/*`, 10/sec elsewhere; a 429 locks you out 30 s and "it is not acceptable to ignore HTTP 429 responses." **`*.scryfall.io` file origins are unmetered**, so the image pull for the hash build is fast (~0.74 GB at `small`, ~1 hour).
- Bulk data is **gzipped JSONL**: field `jsonl_download_uri`, size field `compressed_size`. The old `download_uri`/`size` fields are gone and most tutorials are stale. Resolve via `GET /bulk-data/{type}`; filenames carry a daily timestamp, so never hardcode. Use **`unique_artwork`**.
- Counts: 32,992 oracle names, 53,482 unique arts, 98,578 prints.
- Display terms forbid distorting/blurring/cropping-off the copyright line on images *shown to users*, and require artist credit alongside an art crop. Internal transforms for hashing are fine.

**Never commit card imagery** — not Scryfall renders, and not your own photographs either; the artwork is WotC IP regardless of who shot it. The fixture corpus stays local and gitignored. Commit only *derived* data: the ~8.6 MB hash index and accuracy tables. CI therefore runs against synthetically generated frames. README carries a **WotC Fan Content Policy disclaimer**.

### Platform

Ship **`win-x64` binary only**. But the codebase stays portable — no project named `.Windows`, no gratuitous `#if WINDOWS`, and the `macos-latest` CI leg exists to enforce that. Everything except FlashCap's macOS capture backend already runs on Apple Silicon, mostly as a side effect of dropping OCR. macOS is documented as "should work, unsupported, build from source"; supporting it properly would mean `Info.plist` camera entitlements, Gatekeeper instructions, a `.app` bundle and Mac-camera testing — which is exactly the trap the original plan fell into by building *for* two platforms.

### Git identity

Repo-local, **no global config touched** (the global default on these machines is a work address):

- `user.name` = `jnapoli87`, `user.email` = `jnapoli87@users.noreply.github.com` — a noreply address, because commits publish whatever email they carry and public history is hard to rewrite.
- `core.sshCommand` pins `~/.ssh/id_ed25519_personal` (GitHub won't accept one key on two accounts).
- `core.hooksPath = hooks`, with **`hooks/pre-commit` enforcing three hard rules** — commit identity, no card imagery, and CardSpotter attribution. Tracked, so it survives a fresh clone (where `.git/hooks/` would not) and applies inside every worktree.

**What the hook checks, and why each detail matters:**

| Check | Detail |
|---|---|
| Identity | Uses `git var GIT_AUTHOR_IDENT` / `GIT_COMMITTER_IDENT`, **not** `git config user.email` — `git var` reports the identity git will *actually* use, catching `GIT_AUTHOR_EMAIL` overrides a config lookup would miss. Author and committer are checked independently. |
| Identity | Accepts both noreply forms via `^([0-9]+\+)?jnapoli87@users\.noreply\.github\.com$` — GitHub's web UI uses the ID-prefixed variant, so rejecting it would block every web edit. |
| Imagery | Rejects staged raster files outside `src/LoreFetch.App/Assets/` and `docs/img/`. This is the layer that catches **`git add -f`**, which bypasses `.gitignore` entirely. |
| Attribution | Requires `THIRD-PARTY-NOTICES` to retain the CardSpotter **credit, copyright holder, BSD-3 conditions *and* disclaimer** — all four. BSD-3-Clause requires "this list of conditions" be retained, so a bare "uses CardSpotter" credit does not satisfy it, and trimming the file to one is the realistic way this rots. Checked against the working tree, not the index: the obligation is about what the repo *contains*, not what this commit touched. |
| Attribution | Also prints an **advisory** (non-blocking) list of `PackageReference` ids absent from the notices file, since packages legitimately land before their notice entry does. |

**Why attribution is a git hook rather than CI or a Claude hook:** a git hook binds every commit from any tool by any author — which is the only thing that covers working on this repo without Claude. A Claude hook binds only Claude, and CI catches a licence violation on the wrong side of the push.

⚠️ **Do not rewrite that identity regex as `^(|[0-9]+\+)…`.** BSD grep on macOS rejects an empty alternative with *"empty (sub)expression"* and then matches nothing — which silently converts the guard into "refuse every commit." That bug was in the first version and only surfaced because the hook was tested rather than eyeballed. **An untested guard is not a guard.**

`gh` CLI auth is per-host, not per-repo, and on these machines it is bound to a separate work account — so create repos in the browser, not via `gh`.

---

## The one gate that matters most

**A Scryfall render must retrieve *itself* at Hamming distance ≈ 0 through the full query path.** If the reference-side and query-side transforms diverge at all — a different resize interpolation, a different region offset — matching degrades *silently* rather than failing loudly. This is the highest-risk failure mode in the project. Guard it with a round-trip test before building anything on top.

## Real risks, in order

1. **Glare, focus and tilt — not resolution.** These are the top four items in Wizards' own SpellTable troubleshooting list, and they destroy the local-median bit pattern. Mitigation is physical: diffuse off-axis lighting (a SAD lamp works; position it at a shallow angle, not beside the camera, and check for PWM flicker banding against the 30 fps shutter). Colour temperature is irrelevant — we grayscale before hashing.
2. **Reference/query transform divergence** — see the gate above.
3. **False card detections** — filter on aspect *and* area, log every rejection.
4. **Mat contrast.** Detection depends on finding the card's edge, and modern cards are black-bordered, so a *dark* mat is the worst case — which is what the original plan recommended four times. Settle it by measurement.
5. **Same-art printings are permanently indistinguishable** by hash. Accepted (oracle name only), but say so in the README rather than letting users discover it.
6. **Foils.** Glare defeats hashing without polarized or diffuse light. Out of v1 scope — document the failure rather than hiding it.
7. **`OpenCvSharp4.runtime.osx.arm64` has exactly one release** (2026-06-27, ~6k downloads). No bug reports, which may mean "works" or "unused". Dev-only. Don't confuse it with `OpenCvSharp4.runtime.osx_arm64` (underscore), an unofficial package.

## Working notes

### Everything lives in this repository

**No project state is stored outside it** — not in `~/.claude/plans/`, not in directory-keyed memory, not in a scratchpad. This is a hard rule, not a preference.

The original plan was lost exactly that way: its memory pointed at `~/.claude/plans/snug-forging-salamander.md`, that file no longer existed, and recovering the plan took a filesystem-wide search that eventually turned up a copy in Obsidian. Directory-keyed memory also silently stops loading the moment a project is renamed or moved, which is precisely what happened here.

So: every decision, plan, contract and test strategy is a tracked file in `docs/` or this file. If it matters and it isn't in git, it doesn't exist.

### Card imagery is enforced in two layers, not just documented

The artwork is Wizards of the Coast IP regardless of who photographed it, so neither Scryfall renders nor our own captures may be committed. Once artwork is in history it is there permanently, short of a rewrite — so this is enforced twice:

1. **`.gitignore`** blocks all raster formats tree-wide, opting UI/doc assets back in individually, so a stray fixture can't slip through on `git add -A`. Verify with `git check-ignore -v <path>`.
2. **`hooks/pre-commit`** rejects staged raster files outside the allowed asset paths — which catches `git add -f`, the one move that bypasses `.gitignore` completely.

Only **derived** data is committed: the ~8.6 MB hash index and the accuracy tables. The raw Scryfall downloads that build the index are ignored, as is the user's own `collection.csv`.

### Claude Code hooks — the rules that were previously only discipline

`.claude/settings.json` (committed, so it applies in every worktree) wires two `PreToolUse` guards in `.claude/hooks/`:

| Guard | Rule | Behaviour |
|---|---|---|
| `guard-write.sh` | Project state stays in the repo | **Denies** writes outside the repo. Claude's scratchpad and `/tmp` are exempt — temp files are legitimate, they just are not project state. |
| `guard-write.sh` | Frozen contract surface | **Denies** edits to `Core/Abstractions/**`, `*.csproj`, `*.slnx` **only inside a linked worktree**, so Stream 0 can still author them on `main`. Detected via `--absolute-git-dir` ≠ `--git-common-dir`. |
| `guard-bash.sh` | No force-push | **Denies** `--force`, `-f`, `--force-with-lease` on any `git … push`. The repo is public; rewriting history is unrecoverable for anyone who cloned it, and history is the audit trail for authorship and the imagery rule. |
| `guard-bash.sh` | Publishing needs prior review | **Warns** (does not block) on plain `git push`, `gh pr create`, `gh repo create`, `gh release create`. Deliberately a tripwire rather than a wall — explicit go-aheads do happen, and a block would make pushing impossible. |

Both were tested by piping synthetic payloads (16 cases). One defect surfaced: the force-push pattern originally matched only flag *names* between `git` and `push`, so `git -C /repo push --force` slipped through — a flag *value* broke it. The pattern is now deliberately loose, which is the safe direction, because the deny additionally requires a force flag.

**After changing these, run `/hooks` or restart the session** — the settings watcher only watches directories that already had a settings file at session start, so a newly created `.claude/settings.json` is not live until then.

### Other standing rules

- **The README is written incrementally**, one section per stream as each earns it — not as a lump at the end.
- **Nothing lands on GitHub without review first.** Draft, show the content, wait for explicit approval, *then* push. Local commits on a branch are fine; anything that becomes visible to other people is not.
- **`Core/Abstractions` and every `.csproj` are frozen** once the streams fork. If a stream needs a contract change it stops and asks — a unilateral edit there is a four-way merge conflict.
- **Chaos-test every regression test:** re-apply the bug, run only the new test, confirm it fails *for the right reason*, then revert. A test that merely passes may be vacuous. See `docs/TESTING.md`.
