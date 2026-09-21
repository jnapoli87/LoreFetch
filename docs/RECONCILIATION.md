# Reconciliation — stream reviews → one contract surface

Step 3 of the review sequence in [`PLAN.md`](PLAN.md#review-sequence--before-any-code). The four stream reviews ran in isolation and raised **20 proposed contract changes and 20 open questions**. This file is the record of what was decided on each, and why.

> [!NOTE]
> **All 20 contract changes were accepted — four of them in a stronger form than proposed — and all 20 open questions ruled on.** The seam changed in three structural ways: the pipeline gained an async, thread-safe `CaptureAsync` and an error event, two fakes were added because stream A's export view was otherwise unbuildable against fakes at all, and the reference/query transforms are now documented as **deliberately asymmetric**, which rewrites *the one gate that matters most*.

> [!IMPORTANT]
> Six decisions were cheap to change now and expensive or impossible after Stream 0 freezes. **Four were put to the user and are settled** — one of them overriding my call (`net10.0`, not `net8.0`) and one rejecting the premise I argued from (hand-editing CSV was never a requirement). The remaining two are applied and low-stakes. See [Your calls](#your-calls).

**What lives where.** The *findings* stay in each stream doc's own `Plan review findings` section, which is the primary record and is not duplicated here. This file records only the *rulings*. The accepted changes are applied in [`CONTRACTS.md`](CONTRACTS.md); the five stale claims are corrected in [`../CLAUDE.md`](../CLAUDE.md).

| Verdict | Count | Meaning |
|---|---|---|
| **Accepted** | 16 | Applied as proposed. |
| **Accepted+** | 4 | Applied, but changed — the reviewer under-asked. Marked and explained. |
| **Rejected** | 0 | — |

Zero rejections is worth a note rather than a celebration: these were plan reviews against primary sources, before any code existed, so nearly everything raised was either a documented fact the plan had wrong or a gap that would have stalled a stream after the freeze. The reviews were cheap precisely because they ran at the only moment when a contract change costs an edit.

---

## Cross-stream decisions

The items more than one stream reached independently, plus the ones no single stream owned. These are the reason a reconciliation pass exists.

### 1. `FrameGeometry` is post-rotation — A and C, independently

Both proposed clarifying it, and both recommended the same answer. **`Width`/`Height` are the frame as delivered downstream and always equal the `CameraFrame` dimensions a consumer sees; `RotationDegrees` records what the source already applied and is informational.**

Two reviewers who could not see each other's work converged on this, which is the strongest signal in the whole review. The failure it prevents: stream A maps overlay quads through those dimensions, so pre-rotation values put every quad on the wrong axis at the settled 90°, and stream C notes that a field named `RotationDegrees` riding along with each frame is an open invitation to re-apply it. Comment-level change, real bug avoided.

### 2. Thresholds — one origin, two appliers

A found that `CohortTile.Clear()` cannot honour its contract without comparing a distance to `OkDistance`, which flatly contradicted "the scan pipeline is the only place thresholds are applied."

**Decision: the pipeline hands each `CohortTile` both thresholds at construction, and the rule is reworded** — thresholds *originate* only in `ScanSettings` and are *applied* only by the pipeline and the tiles it builds. Nothing in `App`, `Capture`, `Identification` or `Collection` may source or hardcode one. The original wording was the thing that was wrong, not the tile's behaviour.

### 3. Device loss and store failure both needed a route to the user — C and D

C raised that `IScanPipeline` has no error event, so a dead camera surfaces as a preview that silently stops updating; it explicitly deferred this as belonging to `Core/Scanning` and the UI. D raised that Excel holds `collection.csv` open on Windows, so the atomic rename throws during ordinary advertised use. Neither stream owned the fix.

**Decision, taken here because it is exactly the gap between two streams:**

- `IScanPipeline` gains `event Action<FrameSourceException>? SourceFailed`, raised immediately before `RunAsync` faults.
- `FrameSourceException` and `CollectionStoreException` are both added to `Abstractions`, so callers never match on message text.
- A store failure is **recoverable by construction**: `CommitCohortAsync` throws, the caller keeps the pending cohort intact, and the user closes Excel and presses Enter again. Never discard the cohort; never fall back to a non-atomic in-place write to make the error disappear.

"Camera unplugged" is the most likely runtime failure in the application and "Excel has the file" is the most likely store failure. Both were unroutable.

### 4. The pre-fork package set — A, C and D

All three named packages that cannot be added after the freeze, and C additionally needed a logging seam that did not exist on the contract surface at all.

**Decision: one reconciled package table, in [`PLAN.md`](PLAN.md) Stream 0 item 1**, and **`Microsoft.Extensions.Logging.Abstractions` (MIT) is the logging seam** — chosen over a hand-rolled interface because it is the .NET standard, costs one small reference, and needs no adapter for whatever sink gets wired later. Three of stream C's *done-when* criteria are phrased "the log shows…", so without this its only option was `Console.WriteLine`.

`CLAUDE.md`'s own guidance settles the marginal cases: over-reference rather than under-reference, since an unused `PackageReference` costs nothing and a missing one stalls a fork.

### 5. Composition ownership

A could not start: `LoreFetch.App/**` is stream A's, so stream A writes startup — which must construct six collaborators and call `RunAsync` — but the contract gave the pipeline's interface with no constructor, no factory, and no statement of who loads stream B's thresholds file.

**Decision: Stream 0 owns composition.** It writes `ScanPipelineFactory.Create(...)`, `IFrameSourceFactory`, and the thresholds-file loader. Stream A calls two functions and never learns the wiring, never names a concrete class from another stream, and never implements a file format stream B defines. `IScanPipeline` also gains `SourceDescription` so the status line needs no `IFrameSource`, which the "nothing else reads a frame source" rule otherwise forbade.

---

## Stream A — UI

### Contract changes

| # | Proposal | Verdict |
|---|---|---|
| A1 | `StubCollectionStore` + `StubCollectionExporter` — five fakes → seven | **Accepted** |
| A2 | `StubOracleCatalog` entry count configurable | **Accepted** |
| A3 | `ScanSettings.AutoCaptureEnabled` | **Accepted** |
| A4 | `ScanSettings.MovementTolerancePixels` | **Accepted** |
| A5 | Document `Capture()` thread-safety and `AutoCaptured`'s thread | **Accepted+** |
| A6 | Publish pipeline construction and composition ownership | **Accepted** |
| A7 | `FrameGeometry` pre/post-rotation | **Accepted** — see cross-stream 1 |
| A8 | Pin `Avalonia.Headless.XUnit`; decide `AvaloniaUI.DiagnosticsSupport` | **Accepted** |

**A1 — accepted, and it was right to call it the highest-priority item.** `CONTRACTS.md` promised "stream A never needs anything real from B, C or D — not at the start, not at the end", and that promise was false for A7, A8 and one *done-when* criterion. `StubCollectionStore` carries the real dedup and commit semantics and can be told to throw `CollectionStoreException` on the next commit, so the Excel-lock path is renderable. `StubCollectionExporter` takes a configurable `ExportFormat`, so A registers one verified and one unverified instance and exercises the `IsVerified` badge without a second class.

**A2 — accepted, default ~33,000 synthetic entries**, with the hostile handful always present: commas, embedded double quotes, a **leading** double quote, accents, and `+2 Mace`. A few hundred entries cannot reproduce the type-ahead's only real performance problem. The leading-quote and formula-character names come from stream D's research and are folded in here so both streams test the same hostile set.

**A5 — Accepted+, upgraded from documentation to a signature change.** A asked for the thread-safety to be *documented*; documenting it would have pinned down a design that is wrong. Two defects, one answer:

- `Capture()` reads the retained frame while the pipeline thread replaces and disposes that exact frame — so Space can rectify a buffer already returned to the pool. The retained frame and snapshot are now guarded by a lock, and `CaptureAsync` *takes ownership* under that lock before doing any work outside it.
- `Capture()` was synchronous and does up to nine rectify-and-identify passes inline, i.e. a visible freeze on every Space press. It is now **`Task<Cohort?> CaptureAsync(CancellationToken ct)`**.

Both fixes are free today and impossible after the freeze. `AutoCaptured` is documented as raised on the pipeline's background thread, like `FrameProcessed`.

**A8 — both pinned.** Over-reference rather than under-reference.

### Open questions

1. **Avalonia 12 DevTools licensing — primary sources conflict.** **Decision: pin `AvaloniaUI.DiagnosticsSupport` and accept its absence if it turns out to need a paid tier.** It is a dev-time convenience, nothing is redistributed, so there is no licence obligation for a GPLv3 project either way. Resolve it in stream A's first hour, not at 2 a.m. → [Your calls](#your-calls).
2. **How `CohortTile.Clear()` gets a threshold.** **Decided** — cross-stream 2.
3. **Who loads the thresholds file.** **Decision: Stream 0**, next to the pipeline. Stream A renders the missing-file failure (A8) and nothing more.
4. **Monotonic `now` for the trigger.** **Decision: yes.** The pipeline derives `now` as `startWallClock + stopwatch.Elapsed` rather than calling `DateTimeOffset.UtcNow`. Wall clock can step backwards on an NTP correction — which stalls a 500 ms settle indefinitely — or jump forward, which satisfies it instantly. No signature change; the trigger stays a pure function of what it is handed, which is what makes it testable.
5. **`net8.0` or `net10.0`.** **Decision: `net10.0` — the user's call, overriding my `net8.0`.** Stream A's own stated reason for `net8.0` ("wider compatibility") does not hold anyway: a self-contained single-file publish bundles the runtime, so the user's machine never sees the TFM. That left only "the pins were verified against `net8.0`", against `net10.0`'s longer support life — and `net8.0` leaves LTS in Nov 2026.

   **The risk this takes on, and where it is paid:** no pin was verified against `net10.0` during review. Stream 0 item 1 therefore asserts a clean restore *and* build of every project on `net10.0` **before the freeze**, and falls back to `net8.0` there if any pin fails. Discovering it after the fork is the expensive case, because `.csproj` files are hook-enforced frozen from that point — so this must be proven in Stream 0, not assumed.

---

## Stream B — Identification

### Contract changes

| # | Proposal | Verdict |
|---|---|---|
| B1 | `CardCandidate.ArtworkId` | **Accepted** |
| B2 | `Identify`'s `maxCandidates` = distinct `OracleId` | **Accepted** |

**B1 — accepted, though B itself called it optional.** B offered a workaround (assert against the concrete `Core/Identification` type) and wrote B2's gate around it. Taking the workaround would leave the *public* seam unable to express which artwork matched, and the round-trip gate is the project's single most important test. `string? ArtworkId` is free — the printing id is already in the bulk record — and nullable so the fakes leave it unset. It also lets the UI distinguish "same card, different art" runners-up later.

**B2 — accepted.** Ranking by nearest *entries* lets a heavily reprinted card fill the whole top-5 with its own artworks, which would make B6's distance margin measure art-vs-art of one card instead of a genuine confusion. That margin is what calibrates `GoodDistance`/`OkDistance`, so the thresholds would have been calibrated against the wrong quantity — a silent, permanent error in the number everything else trusts.

### Open questions

1. **Keep the reference/query asymmetry, or force symmetry?** **Decision: keep the asymmetry; `CLAUDE.md` is what moves.** B is right and the document contradicted itself: the seven steps demanded an identical transform while the paragraph justifying them argued that blurring *the reference side* is what makes photo and render converge. Upstream blurs and downsamples the reference only. Forcing symmetry would delete the mechanism that makes the algorithm work.

   **What this rewrites.** *The one gate that matters most* no longer says "distance ≈ 0". The invariant is now: each side's transform exists exactly once, the shared steps (4–6) are bit-identical, and neither side changes without rebuilding the index — guarded by committed golden hashes plus a round-trip test asserting the **measured floor as a bound**. This is the largest single change in the reconciliation. → [Your calls](#your-calls).

2. **~6.0 GB `normal` pull, or stay on `small`?** **Decision: pull `normal`.** `small` is 146×204 while the canonical `RectifiedCard` is 488×680 — exactly Scryfall `normal`. From `small`, the reference side's 96 px step is a 1.5× downscale while the query side's is 5.1×: different box filters over different source detail, so the two sides can never converge and the floor is permanently and *invisibly* inflated. The origin is unmetered, so the cost is wall clock and ~6 GB of scratch disk, not rate limit. Fallback is `border_crop` (480×680); never `small`. → [Your calls](#your-calls).
3. **Filter the index to v1 scope, or carry everything?** **Decision: B's recommendation — filter by *kind*, not by frame.** Drop the 3,440 objects with no top-level `image_uris` (multi-faced), plus `art_series` and `token` layouts and non-English. Keep all frames. Filtering to `frame == "2015"` would shrink the impostor pool and *raise* measured accuracy while making every out-of-scope card return a confident wrong answer — and identification is opt-out, so a confident wrong answer is permanent bad inventory. Accuracy is then reported on modern-frame fixtures only. This keeps the honest failure mode *and* the honest metric.
4. **Hash crop-scale variations?** **Decision: measure the distance floor against crop scale first** — a one-afternoon `Lab` experiment that determines whether ≥90% correct@1 is reachable with one hash per card. Budget a 3-scale sweep as contingency. The **180° orientation pair is not optional** and is already in B5; upstream treats it as mandatory.
5. **Two `THIRD-PARTY-NOTICES` fixes stream B could not make.** **Decision: both applied here.** The copyright line *"Copyright (c) 2019, Jonas Gillberg"* is confirmed against the upstream LICENSE, so the ⚠ VERIFY block is replaced with the verification. The cited files are corrected from the non-existent `Code/ImageHash.*` to `Code/CardData.h`, `Code/CardData.cpp` and `Code/QueryThread.cpp`. Attribution accuracy is a BSD-3 obligation, so this was not left to leisure. The pre-commit hook's four required markers were re-checked after the edit.

### Corrections adopted into `CLAUDE.md`

Stream B's corrections were not contract changes but several invalidated settled text, so they are folded in:

- **Do not port upstream's early rejection.** It is threshold-keyed and inadmissible — it prunes cards beyond a distance rather than cards that cannot make the top N, so it changes the ranking, and it directly contradicts `ICardIdentifier`'s "NEVER filters by threshold". It buys nothing: brute force is **0.243 ms** measured per query, 2.19 ms for a 9-card cohort, not the "microseconds" claimed.
- **Step 3 is `INTER_AREA` by choice, not by fidelity.** Upstream passes `cv::INTER_AREA` as `resize`'s 4th positional argument — which is `double fx` — so it silently runs `INTER_LINEAR`. That is an upstream bug. We build our own index, so what matters is that our two sides agree, not that they match CardSpotter's binaries. A port that "faithfully" copies the call inherits the bug.
- **`warpPerspective` cannot use `INTER_AREA`** — OpenCV documents it as unsupported there. The warp pins `INTER_LINEAR`; "`INTER_AREA` on both sides" is true of every *resize*, not of the warp.
- **Counts re-measured** against the live bulk file: 54,963 arts over 37,926 oracle ids; 48,713 over 33,578 after the scope filter. Index payload 6.0 MiB, ~8.2 MiB with the name table — the old "~8.6 MB from ~67k arts" was arithmetic on a count that was never real.
- **New risk, and it needed an owner: `INTER_AREA` is not bit-exact across x86-64 and ARM64** (carotene's NEON HAL; OpenCV #24163 confirmed, #22477 closed won't-fix). An index built on the Mac may not match queries hashed on Windows — presenting exactly as "degrades silently", the project's stated top risk. **Decision: the index is built and committed on `win-x64`, the ship target, and the golden-hash test runs on both CI legs** so a divergence fails loudly instead of quietly inflating every distance. `GaussianBlur` 3×3 σ=1 on 8-bit is safe — passing σ explicitly forces OpenCV's bit-exact fixed-point path. → [Your calls](#your-calls).

---

## Stream C — Capture

### Contract changes

| # | Proposal | Verdict |
|---|---|---|
| C1 | `LoreFetch.Capture.csproj` package set | **Accepted** — see cross-stream 4 |
| C2 | A logging seam | **Accepted+** |
| C3 | `IFrameSourceFactory` with async create | **Accepted** |
| C4 | `FrameSourceException` | **Accepted** |
| C5 | `FrameGeometry` comment clarification | **Accepted** — see cross-stream 1 |
| C6 | `ScanSettings`: timeouts, device-id format, rotation constraint | **Accepted+** |

**C2 — Accepted+.** C asked for "a logging seam in `Core/Abstractions`, or an agreed package reference for one" and left the choice open. Named explicitly: `Microsoft.Extensions.Logging.Abstractions`, threaded through both factories. An unspecified seam is the kind of thing four streams each solve differently.

**C3 — accepted, and it resolves a real ordering problem.** `Description` and `Geometry` must report the *negotiated* format, but negotiation is `await descriptor.OpenAsync(...)`. Without a factory, either both properties are wrong until the first frame arrives, or a "no such device" failure fires from inside the enumerator rather than at startup. It also means stream A never names stream C's concrete class.

**C6 — Accepted+, with the values pinned rather than left to the implementation.** `FirstFrameTimeoutMs = 10_000` — deliberately generous, because C's own research measured Media Foundation at 5.71 s to first frame at 1080p, so a 5 s budget would fail a working camera. `FrameWatchdogMs = 2_000`. `CameraRotationDegrees` gets a **validating setter** that throws on anything but 0/90/180/270. `PreferredDeviceId` is documented as backend-prefixed, which matters more than it looks: Windows enumeration concatenates three backends, so an unprefixed id silently stops matching if the preference order ever changes between runs.

These two timeouts are not tuning knobs — they are the *entire* mechanism for detecting device failure, because device-in-use, permission-denied and unplug are **not exceptions** from the backend. All three present as frames that simply never arrive.

### Open questions

1. **Which Windows backend?** **Decision: DirectShow preferred, Media Foundation fallback, Video for Windows ignored.** DirectShow is what the latency evidence covers (1.44 s to first frame vs 5.71 s); Video for Windows is a legacy path that misreports modern modes. Fixed rather than discovered, because `PreferredDeviceId` persists a backend-specific identity.
2. **Does the new AVFoundation backend change macOS status?** **Decision: no.** `win-x64` only, macOS compile-only in CI, and explicitly **no device-opening test on the `macos-latest` leg**. C is right that `CLAUDE.md`'s stated reason was stale — the backend has existed since FlashCap 1.11.0 — but issue #182 reports it crashing natively and delivering mis-channelled colour, so the new fact *strengthens* the existing decision. The reason is corrected; the decision is untouched.
3. **Is OpenCvSharp acceptable as a `LoreFetch.Capture` dependency?** **Decision: yes.** `CONTRACTS.md` bars OpenCvSharp *types from the contract surface*, not the dependency; `Core` already references it. No OpenCvSharp type crosses the seam and `CameraFrame` stays a pooled `byte[]`. The alternatives are all worse: `System.Drawing.Common` throws off-Windows, SkiaSharp would couple capture to the UI stack, and nothing new can be added post-fork anyway.
4. **How does device loss reach the user?** **Decided** — cross-stream 3.
5. **Is rotation cost worth measuring?** **Decision: drop it as a planned risk.** The research target's concern was *managed*-code rotation; with `Cv2.Rotate` this is a native transpose-and-flip, so the premise largely dissolves. C6's per-frame decode instrumentation will expose any rotation cost sitting next to it in the same log.

### Corrections worth carrying forward

- **FlashCap does not decode MJPEG** — it hands back JPEG bytes as-is. The plan went straight from "select JPEG" to "rotate" holding a compressed buffer, with no decode task and no package reference for one. This is the single largest reason C1 had to be accepted in full.
- **FlashCap's queue drops the *newest* frame when full**, the inverse of newest-frame-only. And a capacity-1 `DropOldest` channel "removes and ignores" its evicted item **without disposing it**, so every dropped frame leaks its pooled buffer — under precisely the slow-consumer test meant to prove pooling works. `IFrameSource`'s contract comment now says this outright and points at the `itemDropped` overload.
- The "~100–150 lines" estimate is really 250–350 plus tests.

---

## Stream D — Collection & export

### Contract changes

| # | Proposal | Verdict |
|---|---|---|
| D1 | Scope the BOM mandate to the native format | **Accepted+** |
| D2 | `CollectionRow.Condition` — canonical `null` | **Accepted** |
| D3 | Add `OracleEntry`, `TileState`, `RowSource` to D's *Consumes* line | **Accepted** |
| D4 | `CommitCohortAsync` return-value semantics | **Accepted+** |

**D1 — Accepted+.** D proposed making the BOM an explicit per-adapter choice; with only one adapter in v1 the choice can simply be made. **Native: UTF-8 with BOM** (it is the file users open in Excel). **Moxfield: no BOM**, verified by D4's real import. A BOM that fixes Excel can break an importer's header match, and Moxfield's tolerance is undocumented.

**D2 — accepted, and it closes a silent-corruption path.** `Condition` is half the dedup key. `null` is now the *only* representation of "unassessed": it serialises to a blank field and a blank field parses back to `null`, never `""`. The contract previously specified the write direction only, so a CSV round-trip would naturally read a blank field back as `""`, which would not dedup against `null` — quietly splitting one card into two rows. That is exactly this stream's stated "user data silently corrupted" risk, reachable through ordinary use.

**D3 — accepted, trivially.** All three types are already in `Abstractions`; the *Consumes* line is what the freeze is checked against, and `CohortTile.Chosen` is an `OracleEntry?`, the commit filter switches on `TileState`, and `Source` is a `RowSource`. An enumeration gap, not a new dependency.

**D4 — Accepted+.** D asked for the meaning to be *pinned down* between two candidates. Picked: **the number of cards committed** — the sum of quantity increments, not rows touched. Once within-cohort duplicates are folded, committing nine basic lands touches one row, and a UI reporting "1 card added" reads as a bug.

### Open questions

1. **Which second adapter replaces ManaBox?** **Decided by you: one export beyond the native SOT is enough for v1 → Moxfield only.** ManaBox cannot accept name-only rows at all (its docs require card name *plus* set, or a Scryfall ID), so D2's "highest-value pair" did not exist as written. Moxfield is the one researched tool that provably takes name-only rows — only `Name` is required and column order is explicitly irrelevant. ManaBox, Archidekt, Deckbox and Dragon Shield are documented in the README as unsupported-by-design with the one-line reason each; the table is in `CONTRACTS.md`. Deckbox is the strongest candidate if a second is ever added.
2. **Pre-reference `CsvHelper`?** **Decision: yes, pin it.** The technical answer is that no package is required — `TextFieldParser` ships in the shared framework, an RFC 4180 writer is a few lines, and no library provides the unknown-column guard anyway. But `.csproj` files are hook-enforced frozen, so this is insurance, not design, and an unused reference costs nothing. Apache-2.0 under its dual licence, so GPLv3-compatible.
3. **What if Excel holds `collection.csv` open?** **Decided** — cross-stream 3.
4. **Duplicate `OracleId` + `Condition` rows on read?** **Decision: merge — sum `Quantity`, keep the latest `LastScannedAt` — and log it.** Confirmed by the user, who also rejected the premise it was originally argued from: *"idk why hand editing the collection is an advertised feature, no one asked for that, but yes, we should be able to import."*

   That is a correction worth keeping. The justification is **import robustness**, not hand-editing: the reader must cope with a well-formed file it did not write, and a duplicate key is the one malformation with an unambiguous correct answer, where a shifted or unknown *column* is not. `CLAUDE.md`'s CSV rationale has been amended to demote "users can hand-fix a bad row in Excel" from a design driver to a consequence — the remedy for a wrong match is the opt-out grid, and after the fact the `BestMatchDistance` + `Source` query. The CSV decision itself is untouched; it never needed that argument.
5. **Document or mitigate Excel's formula quirk?** **Decision: document, never sanitise.** `+2 Mace` is a real in-scope card and the only oracle name starting with a character Excel evaluates. A `'` or tab prefix would corrupt the source of truth for every machine reader in order to fix one program's rendering. README, known limitations.

### Corrections worth carrying forward

- **The escaping test vectors missed the worst class.** Five oracle names *begin* with a double quote, and `"Rumors of My Death . . ."` contains no comma — so a writer that quotes only when it sees a comma emits it bare, and any conforming reader then mis-parses the row. `Kongming, "Sleeping Dragon"` does not catch that. Folded into `StubOracleCatalog`'s hostile set so stream A tests it too.
- **Scale:** 4,014 card names contain a comma, 134 contain non-ASCII, 12 contain an embedded double quote.
- **ManaBox's `Scryfall ID` is a printing id, not `oracle_id`** — verified by resolving a real sample. Writing ours there would look precise and resolve wrongly. Relevant even though ManaBox is out of v1, because it is the obvious "just add one more adapter" trap.
- **The atomicity claim was contingent on an unstated detail** — see `CLAUDE.md`. The temp file must live in the target's own directory, and `Flush(true)` must precede the rename.

---

## Your calls

### Settled by the user, 2026-09-21

| # | Decision | Outcome |
|---|---|---|
| 1 | The gate is a **measured distance floor, not ≈ 0**, transforms asymmetric by design (B-Q1) | **Confirmed.** Rewrite stands. |
| 2 | Pull **`normal`** images, ~6.0 GB rather than ~0.79 GB (B-Q2) | **Confirmed.** `border_crop` stays the fallback; never `small`. |
| 3 | Duplicate rows **merge on read**, logged (D-Q4) | **Confirmed — and the premise corrected.** Hand-editing CSV in Excel was never a requirement; the justification is import robustness. `CLAUDE.md`'s CSV rationale amended accordingly. |
| 4 | Target framework (A-Q5) | **Overridden: `net10.0`, not my `net8.0`.** The longer-lived LTS. Cost: no pin was verified against it, so Stream 0 item 1 must prove a clean restore and build before the freeze, falling back to `net8.0` there if one fails. |

### Applied, not separately raised

Both are low-stakes and reversible up to the freeze; flag either if you disagree.

| # | Decision | The consequence to know |
|---|---|---|
| 5 | **Index built and committed on `win-x64` only** | The Mac cannot regenerate a *matching* index, because `INTER_AREA` is not bit-exact across architectures. Development on the Mac is unaffected — only index generation is pinned, and the golden-hash test on both CI legs is what makes a divergence loud. |
| 6 | **`AvaloniaUI.DiagnosticsSupport` pinned** | Primary sources genuinely conflict on whether Avalonia 12's DevTools needs a paid tier. Pinning costs nothing; if it turns out to be paid, stream A ships without DevTools and loses no licence obligation, since nothing is redistributed. |

---

## What changed, by file

| File | Change |
|---|---|
| [`CONTRACTS.md`](CONTRACTS.md) | All 20 accepted contract changes. New: `IFrameSourceFactory`, `FrameSourceException`, `CollectionStoreException`, `ScanPipelineFactory`, `CardCandidate.ArtworkId`, `IScanPipeline.SourceFailed`/`SourceDescription`, `Capture()` → `CaptureAsync`. Seven fakes. Logging section. v1 adapter table. Write-sequence and BOM rules. Six `ScanSettings` additions. |
| [`../CLAUDE.md`](../CLAUDE.md) | Five stale claims corrected: the asymmetric transform and the rewritten gate; the unsourced 60 fps figure removed; the stale macOS rationale; NTFS/APFS atomicity softened with the same-directory temp-file requirement; `small` → `normal` with re-measured counts. Early rejection struck. New cross-architecture risk. |
| [`PLAN.md`](PLAN.md) | Review steps 2 and 3 marked done. Stream 0: the reconciled package table, **`net10.0`** with a must-prove-before-freeze gate, seven fakes, pipeline factory, thresholds loader; estimate ~3.5h → ~4h. Round-trip gate criterion restated as a floor. |
| `../THIRD-PARTY-NOTICES` | Cited files corrected to `Code/CardData.h`, `Code/CardData.cpp`, `Code/QueryThread.cpp`; the ⚠ VERIFY block replaced with the completed verification. |
| `stream-{a,b,c,d}-*.md` | Unchanged here — each carries its own reviewer's inline corrections and findings on branch `review/stream-{a,b,c,d}`, still to be merged. |

## Not done here

- **The four review branches are not merged.** Each touches only its own stream doc, so the merge is conflict-free whenever you want it.
- **Nothing is pushed.** `git branch -r` shows only `origin/main`.
- **No code.** Stream 0 starts on your say-so.
