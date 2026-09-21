# LoreFetch — 24h build plan

The spine: sequencing, streams, integration, endgame. **Decisions live in [`../CLAUDE.md`](../CLAUDE.md)** and are not restated here — one source of truth per fact.

---

## Document map

| Document | Job | Who reviews it |
|---|---|---|
| [`../CLAUDE.md`](../CLAUDE.md) | Every settled decision, and why each rejected path stays rejected. Auto-loads for any session in this repo. | everyone, first |
| [`CONTRACTS.md`](CONTRACTS.md) | The frozen seam that makes parallel streams possible | reviewed **before** streams fork |
| **this file** | Sequencing, Stream 0, integration, endgame, cross-stream risks | spine reviewer |
| [`stream-a-ui.md`](stream-a-ui.md) | Avalonia app, built against fakes | UI reviewer |
| [`stream-b-identification.md`](stream-b-identification.md) | Hash port, index, detection, accuracy | identification reviewer ← *the risky one* |
| [`stream-c-capture.md`](stream-c-capture.md) | FlashCap → `IFrameSource` | capture reviewer |
| [`stream-d-export.md`](stream-d-export.md) | Native SOT format + third-party adapters | export reviewer |
| [`TESTING.md`](TESTING.md) | Five test levels and what each must assert | spine reviewer |

Each stream doc is deliberately **self-contained enough to review in isolation** — it names what it owns, what it consumes, its done-when, its fallbacks, and a *"what a reviewer should scrutinise"* section.

---

## Context

The original plan (recovered from Obsidian; its memory pointed at a deleted file) built everything on **OCR of the card name**, which cannot work at overhead camera distance — the name is ~5 px tall at 20″ against a ~10 px floor. It was discarded wholesale.

The replacement is a **ported perceptual hash** — CardSpotter, BSD-3-Clause, the algorithm Wizards' own SpellTable actually ships, verified by inspecting its production WASM bundle. It works at that distance because it never reads text. Full reasoning, including why embeddings were rejected, is in [`../CLAUDE.md`](../CLAUDE.md).

**Setup:** code on the Apple Silicon Mac; the Windows PC sits beside it with the C920, a PVC overhead gantry, a 3D printer and a difficulty-laddered card collection. Push → pull → build → run is a fast local loop with hardware in reach.

---

## Stream 0 — Foundation · H0–H2 · serial, on `main`

The only strictly serial work. Everything downstream forks from this commit, so **its job is to make the four streams independent**, not to build features.

**Already done and verified:** repo at `~/Repos/LoreFetch`, GPLv3 LICENSE, `.gitignore`, remote `git@github.com:jnapoli87/LoreFetch.git`, and repo-local identity (`user.name`, noreply `user.email`, `core.sshCommand` → personal key, `core.hooksPath`). A test commit is authored correctly.

Remaining:

1. **`hooks/pre-commit`** — the identity guard. `core.hooksPath=hooks` is set but `hooks/` **does not exist**, and git treats a missing hooks path as *silently no hooks*, so nothing is guarded yet. Assert the committing email matches `jnapoli87.*users\.noreply\.github\.com` (accepting both the bare and ID-prefixed `73004017+…` forms — GitHub's web UI uses the latter). Tracked, so it survives a fresh clone where `.git/hooks/` would not. **Test it by deliberately setting the work email and confirming the commit is refused** — an untested guard is not a guard.
2. **All projects, with all package references** — `Core`, `Capture`, `Lab`, `App`, `Tests`. Pin Avalonia 12.1.2 and OpenCvSharp 4.13.0.20260627 now. **This is the main merge-conflict source removed by construction:** if every package a stream needs is already referenced, no stream ever edits a `.csproj`.
3. **`Core/Abstractions`** — everything in [`CONTRACTS.md`](CONTRACTS.md), then **frozen**.
4. **The three fakes** — `FolderFrameSource`, `StubCardDetector`, `StubCardIdentifier` (with configurable distances so the UI can reach all four `TileState` values). These are what make stream A independent forever, and `FolderFrameSource` is the demo path too, not just a test double.
5. **CI** — `windows-latest` **and** `macos-latest`. The macOS leg mechanically enforces that `Core` stays free of Windows-only dependencies.
6. **README skeleton** — title, description, GPLv3 note, WotC Fan Content disclaimer.
7. **Fixture capture** — real C920 frames at several heights (8″/10″/12″/14″/20″), rotated, in 1/3/9 layouts, across the difficulty ladder, with ground truth in a sidecar CSV. **Needs no code** — the Windows Camera app or a throwaway script is fine, which is why it doesn't wait on stream C. **Print the adjustable camera mount first**; it's how heights are reached repeatably.
   - Lighting and mat first: SAD lamp off-axis at a shallow angle, check for PWM banding on a blank frame, and test light/mid/dark mat since black-bordered cards on a dark mat is the worst case for edge detection.
   - **Never commit these images** — gitignored, and backed up outside git.

**Done when:** all four streams can be forked into worktrees and each builds green with nothing stubbed out beyond the intended fakes. The identity guard is proven by a refused commit.

**Gate:** the first push is an external publish — content shown and approved before `git push`.

---

## Streams A–D · fork at H2 · four git worktrees

Sized, not clock-boxed. **It is fine for one to finish long before another** — no stream blocks another by construction.

| Stream | Owns (exclusive write) | Size | Needs hardware? | Risk |
|---|---|---|---|---|
| [**A — UI**](stream-a-ui.md) | `App/**` | ~10h | no | low |
| [**B — Identification**](stream-b-identification.md) | `Core/Identification/**`, `Core/Imaging/**`, `Lab/**` | ~10h | no (fixtures only) | **highest** |
| [**C — Capture**](stream-c-capture.md) | `Capture/**` | ~4h | **yes** | medium |
| [**D — Export**](stream-d-export.md) | `Core/Export/**` | ~3h | no | low |

**Shared and frozen:** `Core/Abstractions/**`, every `.csproj`, `LoreFetch.slnx`.

> **The rule that makes this work:** if a stream needs a contract change, **it stops and asks.** It does not edit `Abstractions` unilaterally. Every unilateral change there is a four-way merge conflict, and the contracts are the entire reason the streams are independent.

Stream B is the one that can invalidate the project — if the hash doesn't work at our geometry, A, C and D were plumbing for nothing. It should start first and get the most scrutiny. Its blast radius is contained though: A, C and D are all agnostic to *which* identifier wins, because they only ever see `ICardIdentifier`.

---

## Integration · begins when A and B have both landed · budget 3–4h

Swap fakes for real implementations: `StubCardIdentifier` → the hash, `StubCardDetector` → the contour detector, `FolderFrameSource` → `WebcamFrameSource`.

This is where the bugs live, and they will be **lifetime and threading bugs**, not logic bugs — because that's what the seams hide:

1. **`RectifiedCard` lifetime versus the cohort grid.** `Cohort` is `IDisposable` and owns its tiles' buffers. If the UI bound them into a long-lived list, the thumbnails are reading freed memory — which looks fine in dev and fails during a demo. Flagged as an open question in [`CONTRACTS.md`](CONTRACTS.md); resolve it here at the latest.
2. **Frame buffer handoff across the capture thread boundary.** Pooled buffers make ownership explicit; verify one owner, one `Dispose`.
3. **Real thresholds replace placeholders.** `GoodDistance` / `OkDistance` come from stream B's calibration. Grep for any hardcoded distance anywhere in A, C or D.
4. **The end-to-end test** from [`TESTING.md`](TESTING.md): synthetic frame → full pipeline → cohort → commit → assert the CSV on disk.

**Done when:** the full loop runs on real frames — detect, rectify, identify, grid, commit, export — with the end-to-end test green in CI.

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
3. **Contract churn after the fork.** The failure mode of the whole parallel structure. Mitigated by freezing `Abstractions` and the stop-and-ask rule — but it depends on discipline, so it's a real risk.
4. **Four streams, one reviewer.** Parallelism shifts load from writing to reviewing and integrating. The per-stream reviewer agents are the mitigation; the integration budget is the honest cost.
5. **`OpenCvSharp4.runtime.osx.arm64` has exactly one release** (2026-06-27, ~6k downloads). No bug reports, which may mean "works" or "unused." Dev-only — worst case, local CV testing is lost and we lean on the PC beside us.
6. **The fixture corpus can't be committed**, so CI accuracy runs on synthetic frames while real numbers live locally. Accept the divergence; back the corpus up outside git.

---

## Verification

Per-stream criteria are in the stream docs; test levels in [`TESTING.md`](TESTING.md). The cross-cutting bar:

- `dotnet test` green on Mac **and** `windows-latest` CI.
- Stream B's round-trip gate passes: a Scryfall render retrieves itself at distance ≈ 0 through the full query path.
- The accuracy × height table is committed, measured on **normal cards** (not lands), with both thresholds and the distance-margin data behind them.
- **`wrong@1` ≈ 0.** Failures must be no-match, not confident-wrong — a silent miss is recoverable, a confident wrong answer is permanent bad inventory.
- The whole loop is achievable keyboard-only: space → enter → space → enter, no mouse.
- Live C920 run with **logged proof of 1920×1080 MJPG at 30 fps**, asserted from device characteristics rather than assumed.
- At least one third-party export adapter **actually imported** into its live tool, with the result recorded.
- Fully offline after install: no network needed once the index ships.
- `v0.1.0` self-contained exe runs on a clean Windows profile with no .NET installed and no loose native DLLs beside it.
- Every commit authored as `jnapoli87 <…users.noreply.github.com>`; no work address in the log.
- **No card imagery anywhere in git history.**
