# LoreFetch — 24h build plan

The spine: sequencing, streams, integration, endgame. **Decisions live in [`../CLAUDE.md`](../CLAUDE.md)** and are not restated here — one source of truth per fact.

---

## Document map

| Document | Job | Who reviews it |
|---|---|---|
| [`../CLAUDE.md`](../CLAUDE.md) | Every settled decision, and why each rejected path stays rejected. Auto-loads for any session in this repo. | everyone, first |
| [`../CONTEXT.md`](../CONTEXT.md) | The domain glossary — one word per concept | everyone |
| [`CONTRACTS.md`](CONTRACTS.md) | The frozen seam that makes parallel streams possible | architecture review, then reconciliation |
| **this file** | Sequencing, Stream 0, integration, endgame, cross-stream risks | architecture review |
| [`stream-a-ui.md`](stream-a-ui.md) | Avalonia app, built against fakes | stream A reviewer |
| [`stream-b-identification.md`](stream-b-identification.md) | Hash port, index, detection, accuracy | stream B reviewer ← *the risky one* |
| [`stream-c-capture.md`](stream-c-capture.md) | FlashCap → `IFrameSource` | stream C reviewer |
| [`stream-d-export.md`](stream-d-export.md) | Collection store, native format, third-party adapters | stream D reviewer |
| [`TESTING.md`](TESTING.md) | Five test levels and what each must assert | architecture review |
| [`stream-review-directions.md`](stream-review-directions.md) | How the four stream reviews are launched, run and handed back | the orchestrating session |
| [`RECONCILIATION.md`](RECONCILIATION.md) | Every ruling on the four reviews' 20 contract changes and 20 open questions | reconciliation |
| [`orchestration-plan.md`](orchestration-plan.md) | Validation findings, the gated master checklist, and Sonnet-sized work packages for every stream | the orchestrating agent |

Each stream doc is deliberately **self-contained enough to review in isolation** — it names what it owns, what it consumes, its done-when, its fallbacks, a *"what a reviewer should scrutinise"* section for code review, and a *"Plan review: research targets"* section for the pre-build review.

---

## Review sequence — before any code

Stream reviewers may propose contract changes, and Stream 0 freezes the contracts, so the reviews land **before** Stream 0 writes code:

1. **Architecture review** — one full-context session. Settles boundaries, ownership and the contract surface, so each stream reviewer inherits a correct seam instead of reviewing a moving target. *Done 2026-09-21; merged to `main`.*
2. **Four stream reviews, in parallel** — each in its own worktree forked from `main`, reading only its stream doc, `CONTRACTS.md` and `CONTEXT.md` (plus `CLAUDE.md`, which auto-loads). Isolation is the point: no cross-stream context muddying the research. See [`stream-review-directions.md`](stream-review-directions.md). *Done 2026-09-21; branches `review/stream-a|b|c|d`.*
3. **Reconciliation** — one full-context session reads all four "Proposed contract changes" sections *against each other* and applies the accepted ones to `CONTRACTS.md` once. Skipping this means four proposals get applied on their own terms. *Done 2026-09-21 — 20 contract changes and 20 open questions ruled on; decision record in [`RECONCILIATION.md`](RECONCILIATION.md).*
4. **Stream 0** builds and freezes.
5. **Streams A–D fork.**

---

## Context

The original plan (recovered from Obsidian; its memory pointed at a deleted file) built everything on **OCR of the card name**, which cannot work at overhead camera distance — the name is ~5 px tall at 20″ against a ~10 px floor. It was discarded wholesale.

The replacement is a **ported perceptual hash** — CardSpotter, BSD-3-Clause, the algorithm Wizards' own SpellTable actually ships, verified by inspecting its production WASM bundle. It works at that distance because it never reads text. Full reasoning, including why embeddings were rejected, is in [`../CLAUDE.md`](../CLAUDE.md).

**Setup:** code on the Apple Silicon Mac; the Windows PC sits beside it with the C920, a PVC overhead gantry, a 3D printer and a difficulty-laddered card collection. Push → pull → build → run is a fast local loop with hardware in reach.

---

## Stream 0 — Foundation · H0–~H3.5 · serial, on `main`

The only strictly serial work. Everything downstream forks from this commit, so **its job is to make the four streams independent**, not to build features.

**Already done and verified:**

- Repo at `~/Repos/LoreFetch`, GPLv3 LICENSE, remote `git@github.com:jnapoli87/LoreFetch.git`.
- Repo-local identity: `user.name`, noreply `user.email`, `core.sshCommand` → personal key, `core.hooksPath`. No global config touched.
- `.gitignore` blocks raster files tree-wide with UI/doc assets opted back in individually, so a stray fixture can't ride in on `git add -A`.
- **`hooks/pre-commit` — written and tested against 14 cases.** Three checks: commit **identity** (rejecting the work address from config *or* `GIT_AUTHOR_EMAIL`/`GIT_COMMITTER_EMAIL`, while accepting both the bare and GitHub's ID-prefixed noreply forms); staged **imagery** outside the allowed asset paths, which catches the `git add -f` bypass that `.gitignore` cannot; and **`THIRD-PARTY-NOTICES` attribution** — CardSpotter's credit, copyright holder, BSD-3 conditions and disclaimer must all survive, since a bare credit line does not satisfy BSD-3-Clause. Tracked, so it survives a fresh clone and applies in every worktree.
  - Attribution sits in a *git* hook rather than CI or a Claude hook deliberately: a git hook binds every commit from any tool by any author, which is the only thing that covers working on this repo without Claude. CI would catch a licence violation on the wrong side of the push.
  - *Testing caught a real defect:* the first regex used an empty alternative (`^(|[0-9]+\+)…`), which BSD grep on macOS rejects with "empty (sub)expression" and then matches nothing — silently turning the guard into "refuse every commit." Fail-closed, but broken. An untested guard is not a guard.
- **Claude Code hooks** in `.claude/` — two `PreToolUse` guards, tested against 16 synthetic payloads. They mechanise the two rules that were previously discipline only: project state stays in the repo, and the **frozen contract surface** is unwritable from inside a linked worktree (detected via `--absolute-git-dir` ≠ `--git-common-dir`, so `main` can still author it). Plus a hard block on force-push and a tripwire warning on plain `git push` / `gh pr create`. See `../CLAUDE.md`.
- All planning documents.

Remaining — **~4h**, up from the original ~2h: the architecture review moved the scan pipeline and two more fakes here, and reconciliation added two further fakes, the pipeline factory and the thresholds loader. They can't move into a stream: the end-to-end suite proves the pipeline, so it must exist before the fork.

0. **Prove `OpenCvSharp4.runtime.osx.arm64` on the Mac, first.** `Core` references OpenCvSharp (for `Core/Imaging` and `FolderFrameSource`), so stream A's demo path depends on a package with exactly one release. Load it and run one `Cv2` call before anything else. If it fails, you find out before the fork, not after.
1. **All projects, with all package references** — `Core`, `Capture`, `Lab`, `App`, `Tests`, all on **`net10.0`**. **This is the main merge-conflict source removed by construction:** if every package a stream needs is already referenced, no stream ever edits a `.csproj`. The reconciled set, every item of which a stream review named as un-addable after the fork:

   | Package | Version | For |
   |---|---|---|
   | `Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Controls.DataGrid` | 12.1.2 | A |
   | `Avalonia.Headless.XUnit` | 12.1.2 | A — the only way to test the keyboard map automatically |
   | `AvaloniaUI.DiagnosticsSupport` | 2.2.3 | A — DevTools; **may require a paid tier**, see `RECONCILIATION.md` |
   | `CommunityToolkit.Mvvm` | 8.4.2 | A |
   | `OpenCvSharp4` | 4.13.0.20260627 | B, C, Core |
   | `OpenCvSharp4.runtime.win` / `.runtime.osx.arm64` | matching | B, C, Core |
   | `OpenCvSharp5.AvaloniaExtensions` | — | A, Mat→screen |
   | `FlashCap` | 1.12.0 | C |
   | `Microsoft.Extensions.Logging.Abstractions` | current | **the logging seam** — C's done-when criteria are phrased "the log shows…" |
   | `CsvHelper` | current (Apache-2.0 option) | D — insurance only; `TextFieldParser` is in-box and no library gives the unknown-column guard |

   **`net10.0`** — the longer-lived LTS; `net8.0` leaves support in Nov 2026, and "wider compatibility" is a non-argument here because a self-contained single-file publish bundles the runtime, so the user's machine never sees the TFM.

   > ⚠ **Prove it before freezing.** Avalonia 12.1.2's nuspec targets `net8.0` **and `net10.0`**, so the most constraining pin is fine; the rest ship `netstandard2.0`/`net8.0`, which `net10.0` consumes normally. That makes this ordinary restore risk rather than a known incompatibility — but it is still unproven, so **this item is not done until every project restores and builds clean on `net10.0`**. If any pin fails, fall back to `net8.0` **here**. After the fork `.csproj` files are hook-enforced frozen, which makes this the last cheap moment to find out.
2. **`Core/Abstractions`** — everything in [`CONTRACTS.md`](CONTRACTS.md), then **frozen**.
3. **`Core/Scanning`** — the scan pipeline: detection loop, capture from the latest snapshot's frame, threshold application, trigger calls. Then **frozen**.
4. **The seven fakes** — `FolderFrameSource`, `StubCardDetector`, `StubRectifier`, `StubCardIdentifier` (configurable distances so the UI can reach all four `TileState` values), `StubOracleCatalog` (**~33k entries, configurable** — a few hundred cannot reproduce the type-ahead's only performance problem), plus **`StubCollectionStore` and `StubCollectionExporter`**, added in reconciliation. Stream A's collection view, empty state and export picker are all built against `ICollectionStore` and `ICollectionExporter`, which belong to stream D — without those two the claim that "stream A never needs anything real from B, C or D" was simply false. These are what make stream A independent forever, and `FolderFrameSource` is the demo path too, not just a test double.
   - Also here: **`ScanPipelineFactory`** and the **thresholds-file loader**. Composition is Stream 0's, so stream A calls one function rather than learning the wiring, and never implements a file format stream B defines.
5. **The end-to-end integration suite, green from the end of Stream 0.** Written here in `Tests/Integration/`, against the fakes, parameterised so real implementations swap in later and skip until their artifacts exist. It tests wiring rather than correctness — contract composition, frame ownership, exclude/discard/clear semantics, commit idempotency, CSV shape. Its frames are **generated at test setup** into a temp folder (plain fills with a drawn rectangle — the stub detector ignores content), so no raster is ever committed. Across four worktrees this is the highest-value guard available: it fails the moment someone breaks a contract. See [`TESTING.md`](TESTING.md).
6. **CI** — `windows-latest` **and** `macos-latest`. The macOS leg mechanically enforces that `Core` stays free of Windows-only dependencies.
7. **Shared-write scaffolding** — `Tests/StreamA|B|C|D/` folders, and a **README skeleton** (title, description, GPLv3 note, WotC Fan Content disclaimer) with **one headed section per stream**. Each stream edits only its own folder and its own section, so parallel edits merge cleanly.
8. **Fixture capture** — real C920 frames at several heights (8″/10″/12″/14″/20″), rotated, in 1/3/9 layouts, across the difficulty ladder, with ground truth in a sidecar CSV. **Needs no code** — the Windows Camera app or a throwaway script is fine, which is why it doesn't wait on stream C. **Print the adjustable camera mount first**; it's how heights are reached repeatably.
   - Lighting and mat first: SAD lamp off-axis at a shallow angle, check for PWM banding on a blank frame, and test light/mid/dark mat since black-bordered cards on a dark mat is the worst case for edge detection.
   - **Never commit these images** — gitignored, and backed up outside git.

**Done when:** all four streams can be forked into worktrees and each builds green with nothing stubbed out beyond the intended fakes. The identity guard is proven by a refused commit.

> ⚠ **Items 1, 2 and 3 are hard blockers on forking, not nice-to-haves.** `.claude/hooks/guard-write.sh` **refuses** edits to `Core/Abstractions/**`, `Core/Scanning/**`, `*.csproj` and `*.slnx` from inside a linked worktree. That is the intended design — it is what makes the frozen contract surface real rather than aspirational — but the consequence is that **a missing package reference or an incomplete contract cannot be fixed from a stream.** Fork four worktrees with a `.csproj` gap and all four are blocked until someone goes back to `main`.
>
> So before creating any worktree: every project exists, every package a stream could plausibly need is already referenced, and `Core/Abstractions` compiles and is reviewed. Over-reference rather than under-reference — an unused `PackageReference` costs nothing, a missing one costs a fork-wide stall.

**Gate:** the first push is an external publish — content shown and approved before `git push`.

---

## Streams A–D · fork at ~H3.5 · four git worktrees

Sized, not clock-boxed. **It is fine for one to finish long before another** — no stream blocks another by construction.

| Stream | Owns (exclusive write) | Size | Needs hardware? | Risk |
|---|---|---|---|---|
| [**A — UI**](stream-a-ui.md) | `App/**`, `Core/Trigger/**` | ~10h | no | low |
| [**B — Identification**](stream-b-identification.md) | `Core/Identification/**`, `Core/Imaging/**`, `Lab/**` | ~10h | no (fixtures only) | **highest** |
| [**C — Capture**](stream-c-capture.md) | `Capture/**` | ~4h | **yes** | medium |
| [**D — Collection & export**](stream-d-export.md) | `Core/Collection/**`, `Core/Export/**` | ~4h | no | low |

Each stream also owns `Tests/Stream<X>/**` and its own README section. Full table, including what each consumes and must not touch: [`CONTRACTS.md`](CONTRACTS.md#stream-boundaries).

**Shared and frozen:** `Core/Abstractions/**`, `Core/Scanning/**`, every `.csproj`, `LoreFetch.slnx`.

> **The rule that makes this work:** if a stream needs a contract change, **it stops and asks.** It does not edit `Abstractions` unilaterally. Every unilateral change there is a four-way merge conflict, and the contracts are the entire reason the streams are independent.

Stream B is the one that can invalidate the project — if the hash doesn't work at our geometry, A, C and D were plumbing for nothing. It should start first and get the most scrutiny. Its blast radius is contained though: A, C and D are all agnostic to *which* identifier wins, because they only ever see `ICardIdentifier`.

---

## Integration · begins when A and B have both landed · budget 3–4h

Swap fakes for real implementations: `StubCardIdentifier` → the hash, `StubCardDetector` → the contour detector, `FolderFrameSource` → `WebcamFrameSource`.

This is where the bugs live, and they will be **lifetime and threading bugs**, not logic bugs — because that's what the seams hide:

1. **Frame retention across the pipeline callback.** `FrameProcessed` hands the UI a frame that is valid only for the callback. If the preview kept a reference instead of copying, it reads a recycled pooled buffer — fine in dev, torn frames under load. (The old `RectifiedCard`-lifetime hazard is gone by construction: rectified cards are unpooled.)
2. **Frame buffer handoff across the capture thread boundary.** Pooled buffers make ownership explicit; verify one owner, one `Dispose`, from `WebcamFrameSource` through the pipeline.
3. **Real thresholds replace placeholders.** `GoodDistance` / `OkDistance` come from stream B's committed thresholds file. Grep for any hardcoded distance anywhere outside `Core/Scanning`.
4. **Flip the skip gate.** The end-to-end suite already exists and has been green since the end of Stream 0 against the fakes ([`TESTING.md`](TESTING.md)); its real-implementation cases have been *skipping* with reasons. Integration is the point where a skipped test becomes a **failing** test, so skips can't quietly become permanent.

**Done when: the skip count reaches zero.** That's the whole definition — the end-to-end suite runs against real implementations throughout, with nothing gated out. Mechanical rather than a judgement call.

---

## Endgame · budget ~7h

### E1 — Hardware calibration, on the PC
Lock the mount at the height stream B's table chose. Re-run the difficulty ladder **live** in all three layouts. Confirm live accuracy matches the corpus numbers within a stated tolerance, and write down the delta. "Predicted X, measured Y" is a strong demo artifact precisely because it's falsifiable.

### E2 — Polish and release
Graceful failure, no stack traces in the UI, loading states. README final pass — its sections have been accruing since Stream 0, so this is install instructions, `THIRD-PARTY-NOTICES` (including CardSpotter's required BSD-3 attribution), and an honest **known limitations** section: foils, same-art printings, the basic-land caveat, and which export adapters are unverified.

Publish `-r win-x64 --self-contained -p:PublishSingleFile=true` **plus `-p:IncludeNativeLibrariesForSelfExtract=true`** — without that flag `OpenCvSharpExtern.dll` ships loose and it isn't single-file. Expect ~150–250 MB. Tag `v0.1.0`, release with the GPLv3 source-availability note. Flag that an unsigned exe trips SmartScreen.

### E3 — The 5-minute demo video · 3h, and don't compress it
A recorded demo is a deliverable, not an afterthought. App footage from `FolderFrameSource` for reliability; real over-the-shoulder footage for the physical beats.

| Time | Beat |
|---|---|
| 0:00–0:45 | The problem: paid subscriptions, phone-at-a-desk, scan speed. |
| 0:45–1:45 | "Everyone assumes you OCR the card name." The pixel math — 5 px text at 20″. Why that's the wrong question. |
| 1:45–2:45 | What SpellTable actually does, and the four choices that make hashing survive a webcam. Our measured accuracy × height table. |
| 2:45–4:00 | The app: space-enter-space-enter through a stack, then a 3×3 batch auto-firing on settle. X out the one it got wrong. Collection builds, export opens in Excel. |
| 4:00–4:40 | The rig: gantry, printed mount, SAD lamp. Where it fails and why — glare, foils. |
| 4:40–5:00 | Free, GPLv3, fully offline, ships its own 8 MB index. Repo link. |

**If E1 overruns, cut polish (E2), not the video.** The recording is the graded artifact.

---

## Cross-stream risks

Stream-specific risks live in each stream doc. These span the whole build:

1. **Glare, focus and tilt — not resolution.** The top items in Wizards' own SpellTable troubleshooting list, and they destroy the local-median bit pattern. Physical mitigation, settled during Stream 0's fixture capture. **This is the most likely reason the project underperforms**, and no amount of code fixes it.
2. **Reference/query transform divergence** (stream B). Degrades matching *silently* rather than failing. Guarded by the round-trip gate; structurally mitigated by the index builder and the scanner sharing one transform function rather than two that agree.
3. **Contract churn after the fork.** The failure mode of the whole parallel structure. Mitigated by freezing `Abstractions` and `Scanning` (hook-enforced), by reviewing the contracts per stream *before* the freeze, and by the stop-and-ask rule.
4. **Four streams, one reviewer.** Parallelism shifts load from writing to reviewing and integrating. The per-stream reviewer agents are the mitigation; the integration budget is the honest cost.
5. **`OpenCvSharp4.runtime.osx.arm64` has exactly one release** (2026-06-27, ~6k downloads). No bug reports, which may mean "works" or "unused." Dev-only — worst case, local CV testing is lost and we lean on the PC beside us.
6. **The fixture corpus can't be committed**, so CI accuracy runs on synthetic frames while real numbers live locally. Accept the divergence; back the corpus up outside git.
7. **FFmpeg notices inside the OpenCvSharp native runtimes are not enumerated.** The NuGet packages declare Apache-2.0, but the native binaries statically link or ship FFmpeg (LGPL-2.1+, or GPL-2+ with `--enable-gpl`). Both upgrade cleanly into GPLv3 so the *licence* is compatible — but the specific notices those libraries require have not been listed in `THIRD-PARTY-NOTICES`. Chase before promoting a release widely. Low risk for a hobby project, non-zero for a public one.

---

## Verification

Per-stream criteria are in the stream docs; test levels in [`TESTING.md`](TESTING.md). The cross-cutting bar:

- `dotnet test` green on Mac **and** `windows-latest` CI.
- Stream B's round-trip gate passes: a Scryfall render retrieves **its own artwork** through the full query path at or below the recorded distance floor (not ≈ 0 — the two sides are asymmetric by design; see [`RECONCILIATION.md`](RECONCILIATION.md)), and the committed golden hashes match on **both** CI legs.
- The accuracy × height table is committed, measured on **normal cards** (not lands), with both thresholds and the distance-margin data behind them.
- **`wrong@1` ≈ 0.** Failures must be no-match, not confident-wrong — a silent miss is recoverable, a confident wrong answer is permanent bad inventory.
- The whole loop is achievable keyboard-only: space → enter → space → enter, no mouse.
- Live C920 run with **logged proof of 1920×1080 MJPG at 30 fps**, asserted from device characteristics rather than assumed.
- At least one third-party export adapter **actually imported** into its live tool, with the result recorded.
- Fully offline after install: no network needed once the index ships.
- `v0.1.0` self-contained exe runs on a clean Windows profile with no .NET installed and no loose native DLLs beside it.
- Every commit authored as `jnapoli87 <…users.noreply.github.com>`; no work address in the log.
- **No card imagery anywhere in git history.**
