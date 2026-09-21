# Orchestration plan

The execution control document for the build. It tells the orchestrating agent what to do, in what order, and when it may proceed. **Decisions live in [`../CLAUDE.md`](../CLAUDE.md), contracts in [`CONTRACTS.md`](CONTRACTS.md), and stream detail in the stream docs.** This file sequences and slices that material. It does not restate it, except where a Sonnet implementer would otherwise act on stale text.

> **User decisions recorded here (2026-09-21), pending propagation by G0.5:**
> - The golden-hash test runs on **Windows only**. The macOS leg filters it out by trait, because `INTER_AREA` is not bit-exact on ARM64. This supersedes "both CI legs" wherever it appears.
> - **Merge gate:** each stream merges once `dotnet build` and `dotnet test` are green locally on the Windows PC. After that come fixed push checkpoints (§3), and each one needs the user's approval. Both CI legs must be green at every push.

## 0. How the orchestrator uses this file

**Roles**
- **Orchestrator.** Runs one long-lived session on `main` in the main checkout, `C:\Repos\LoreFetch`. It owns this file, dispatches packages, verifies them, merges, and ticks checkboxes. It never implements a package itself unless the package is marked 🧭 (orchestrator-only).
- **Implementer.** One Sonnet subagent per package. It works only in its stream's worktree and returns a report. Implementers never push, merge, or edit this file.
- **Human.** Packages marked 👤 need the user: hardware, a Mac, accounts, approving a push, recording the video. The orchestrator asks and waits. It never simulates them.

**Status tracking**
- Checkboxes in this file on `main` are the single source of progress. Tick an item only after its **Accept** step passes. Commit each tick as `Orchestration: <ID> done`. Stream branches never touch this file.
- A **gate** (`⛩ Gx`) opens only when every item listed under it is ticked. **No item downstream of a gate may start before the gate opens.** Items inside one stream run in listed order unless marked ∥ (they may run alongside the previous item).
- Record measured values (the distance floor, thresholds, fps, row counts) inline next to the item that produced them.

**Worktrees**
- Create them at G1: `git worktree add .claude/worktrees/stream-<x> -b stream/<x> main`, for x = a, b, c, d.
- The Agent tool cannot set a working directory. Every brief therefore gives the **absolute worktree path** and requires every shell command to run with that path as its working directory (`git -C <path>` or `dotnet … <path>`).
- Only one implementer runs in a given worktree at a time. Parallelism is across streams, up to 4 implementers at once.

**Package brief template.** Send this verbatim, filling in the fields.
```text
You are implementing LoreFetch package <ID>: <title>.
Worktree (do ALL work here, absolute paths only): <path>   Branch: <branch>
Write scope — you may create/edit ONLY: <globs>. Anything else is out of scope.
Read first: CLAUDE.md (auto-loaded), CONTEXT.md, <exact doc sections>.
Overrides — where the docs say otherwise, these win: <rulings from §1 / package notes>.
Build: <deliverables>.
Done when: <acceptance command(s)> pass, plus <assertions>.
Regression tests: chaos-test each one (docs/TESTING.md §Standing practice) and say in your report that you did.
STOP and report instead of continuing if: you need to edit Core/Abstractions, Core/Scanning,
Core/Fakes, Tests/Integration, any .csproj/.slnx/Directory.*.props/global.json; a contract looks
wrong or insufficient; you need a package that isn't referenced; acceptance cannot pass without
weakening a test; the package is growing past ~400 lines of production code.
Finish: one or more commits on <branch> (repo identity is preconfigured; never bypass the
pre-commit hook), no push. Reply with: files changed, test results, anything deferred, any stop reason.
```

**Verify every package before ticking it** 🧭
1. `git -C <wt> diff --stat <pkg-base>..HEAD`: every path is inside the package's write scope.
2. `dotnet build <wt>/LoreFetch.slnx -c Release` produces 0 errors, and the package's acceptance command passes.
3. `dotnet test <wt>/Tests/Integration`: the fakes suite is still green. It guards the contracts.
4. Read the diff against the stream doc's *What a reviewer should scrutinise* list, and fail the package if any item is violated.
5. On failure, send the implementer the specific defect. After two failed rounds, stop and ask the user.

**Merge gate (per stream, user decision)**
- Local `dotnet build` and `dotnet test` are green on this Windows PC.
- The stream's automatable done-when items are ticked.
- `git merge --no-ff stream/<x>` into `main`, then rerun the full local suite on `main`.
- Then the push checkpoint for that merge (§3). Every push needs the user's explicit approval, with the diff summary shown first, and both CI legs must pass.

## 1. Validation findings

These came from checking the merged plan against itself, the environment on this PC, and NuGet on 2026-09-21. Each one is resolved by a checklist item, or by an override that every relevant brief must carry.

| # | Finding | Resolution |
|---|---|---|
| V1 | **The .NET 10 SDK is not installed on this PC.** `dotnet --list-sdks` shows 7.0.x and 8.0.x only, so Stream 0's `net10.0` pin cannot even be attempted. | G0.1 (👤 install) |
| V2 | **`jq` is not on `PATH`.** `guard-write.sh` fails open without it, so the frozen-surface guard is currently doing nothing. | G0.2 |
| V3 | **`guard-write.sh` decides "linked worktree" from the session's working directory, not from the target file.** A subagent started from `main` (which is how the Agent tool launches them) can write into `.claude/worktrees/stream-x/…/Core/Abstractions` unchallenged. The Windows scratchpad (`/c/Users/*/AppData/Local/Temp/claude/*`) is also not exempt, and neither are the CPM files, `Core/Fakes/` or `Tests/Integration/`. | G0.6 |
| V4 | **Drop `OpenCvSharp5.AvaloniaExtensions`.** Its latest release (5.0.0.20260905) depends on **`OpenCvSharp5`** and on Avalonia **12.1.1**. That is a second, different-major OpenCvSharp binding next to `OpenCvSharp4` 4.13. It is not needed either: frames cross the seam as `byte[]`, and the App blits them into its own `WriteableBitmap`. Its version was also left unpinned ("—"). | **Fixed 2026-09-21** in CLAUDE.md's stack table and PLAN's package table. S0.1 does not reference it. |
| V5 | **The test stack is unpinned.** `Avalonia.Headless.XUnit` 12.1.2 depends on `xunit.v3.extensibility.core` 3.2.2, so the suite must use **xunit v3** (TESTING.md's `Assert.Skip` is also a v3 API). Pin `xunit.v3`, `xunit.runner.visualstudio` 3.x and `Microsoft.NET.Test.Sdk`. Also add `Microsoft.Extensions.Logging` plus a sink (`.Console`), or stream C's "the log shows…" criteria are never visible. "current" is not a pin: pin `CsvHelper` and `M.E.Logging.Abstractions` exactly. | The xunit v3 half was **fixed 2026-09-21** in PLAN's package table and CLAUDE.md. S0.1 still pins exact versions and adds the logging sink. |
| V6 | **TESTING.md is stale on hashing.** It says to assert "properties, not golden byte values" and lists scale invariance. Stream B requires committed golden hashes and says scale is a *bound*, not an invariant. The user has also ruled that goldens run on **Windows only** (INTER_AREA is not bit-exact on ARM64, and `macos-latest` is ARM64). | G0.5 propagates to CLAUDE.md, TESTING.md, stream-b and PLAN.md |
| V7 | **"Integration is done when the skip count reaches zero" cannot be met in CI.** The real-implementation cases need the index, Scryfall renders and fixtures, and imagery can never be committed. **Redefined:** zero artifact-gated skips on the orchestrator's **local win-x64 run** with `LOREFETCH_REQUIRE_REAL=1`, which turns every such skip into a failure. CI skips are allowed and must each state a reason. | S0.6a, I6 |
| V8 | **Stream 0 cannot write `[real]` integration cases.** They reference types that will not exist until B, C and D land, and code that does not compile cannot be skipped. **Resolved:** the suite is parameterised over an `IImplementationSet` provider. `Fakes` exists from S0. `Real` is a stub that skips with a reason until integration fills it in. The concrete type names are fixed now (§4) so wiring them is mechanical. | S0.6a, I1–I3 |
| V9 | **No fake trigger exists**, but the fakes suite must exercise `AutoCaptured`, and the real trigger belongs to stream A. | S0.6a adds a test-local `ScriptedTrigger` in `Tests/Integration` |
| V10 | **`Cohort` and `CohortTile` constructors are unspecified.** D's and A's tests have to construct them. **Resolved:** both get public constructors. `CohortTile(RectifiedCard image, IReadOnlyList<CardCandidate> candidates, int goodDistance, int okDistance)` computes the initial state with the same private function that `Clear()` uses, so the rule is written exactly once. | S0.3b |
| V11 | **The thresholds-file schema, the index location and shipping the index are all unowned.** Stream 0 writes the loader and the frozen `App.csproj` must ship both files. **Resolved:** repo path `data/index/`, holding `cards.lfidx` (format owned by B) and `thresholds.json`, schema v1 in S0.4b. `App.csproj` copies `data/index/**` to the output and publish directories. The index must also carry a per-entry basic-land flag, because B6 excludes lands by `type_line`. | S0.1, S0.4b, B3a |
| V12 | **The pipeline's `maxCards` is unspecified.** Space ignores the expected count, so detection must always ask for the maximum: `maxCards = 9`. The trigger compares the count against `ExpectedCount`. | S0.4a |
| V13 | **CI has no card images to synthesise from.** B7 starts from a Scryfall render. CI synthetic tests therefore use procedurally generated card-like images and a small index built inside the test. Real renders are used only locally. | B7, B2 |
| V14 | **Fixture capture (PLAN S0 item 8) is human hardware work sitting in the serial foundation**, but it gates only B6 and E1. It moves to the parallel human track **H** so it does not hold up the fork. | H1–H3 |
| V15 | **`.claude/worktrees/` and `.idea/` are not gitignored.** `main` already shows `.idea/` and `.claude/skills/` as untracked, so a `git add -A` on `main` could commit embedded worktree gitlinks. The ".gitignore ~8.6 MB" comment is also stale (the real figure is ~8.2 MiB). | G0.4 |
| V16 | **Stale text a literal reader will follow.** Stream B's B2 says to "assert on the concrete type" (it is now `CardCandidate.ArtworkId`). Stream A's A5 says the stub has "a few hundred entries" (it is now ~33k). Stream D's done-when says "Two adapters" (v1 is **native + Moxfield**). D0 item 9 uses the Excel hand-fix premise (rejected, though the behaviour it leads to stands). RECONCILIATION's "branches not merged" is out of date. | The docs are not rewritten. Package notes carry the ruling as an **Override**. |
| V17 | **CLAUDE.md's step 6 omits the tie-break and the upper-order median.** Only stream-b has them. | B1a override |
| V18 | **A single `Tests` project fights xunit v3** (test projects are executables), Avalonia headless's assembly-level test-app attribute, and native OpenCV loading. **Resolved:** one test project per folder: `Tests/StreamA`, `StreamB`, `StreamC`, `StreamD` and `Integration`. Stream 0 creates and freezes them all. | S0.1 |
| V19 | **Only stream C has a factory.** At integration the App must name B's and D's concrete classes. That is acceptable because it happens on `main` after they merge, and the names are fixed in §4. | §4, I1–I3 |
| V20 | **`FolderFrameSource` has no factory.** `IFrameSourceFactory.CreateAsync(ScanSettings)` has nowhere to take a folder path from. **Resolved:** `FolderFrameSourceFactory(string folder, TimeSpan interval)` in `Core/Fakes`. | S0.5a |

## 2. Master checklist

```
G0 preconditions ─► S0 (serial, main) ─► ⛩G1 fork ─┬─ A  (A0…A10)          ─┐
                                                   ├─ B  (B1…B8) critical   ├─► I (integration, main) ─► E (endgame)
   H (human, parallel from G0: mount, light,       ├─ C  (C1…C4) ←H6        │
      fixtures, Moxfield acct) ────────────────────┴─ D  (D1…D6) ←H4        ─┘
                                   H3 fixtures ─► B6 ;  H3 + full index ─► E1
```

### ⛩ G0 — Preconditions (on `main`)
- [x] **G0.1** 👤 Install the .NET 10 SDK. Accept: `dotnet --list-sdks` shows 10.0.x. — *done 2026-09-21: 10.0.401.*
- [x] **G0.2** 👤 Put `jq` on `PATH`, then restart the session. Accept: `jq --version`, and the guard denies a synthetic write to `Core/Abstractions/X.cs` from inside a test worktree. — *done 2026-09-21: jq-1.8.2; guard denied the synthetic write from a linked worktree. The cwd hole (V3) remains for G0.6.*
- [ ] **G0.3** 👤 Confirm Mac access for S0.0 and an attached C920 for H6. If no Mac is available, record that S0.0 is waived and that Risk 7 is accepted.
- [x] **G0.4** 🧭 `.gitignore`: add `.claude/worktrees/`, `.idea/` and `.claude/skills/` (or commit skills deliberately), and fix the size comment. Accept: `git status` on `main` is clean. — *Partly done 2026-09-21: `.claude/worktrees/`, `.idea/` and the size comment are fixed, and `test-images/` was added as the local imagery folder. Still open: decide whether to ignore or commit `.claude/skills/`, then confirm on `main` after the merge.*
  *Finished 2026-09-21: `.claude/skills/` is **ignored** — no project state lives in a skill, and `.claude/hooks/` plus `.claude/settings.json` are the only things that must come from git because they have to apply inside every worktree. `.DS_Store` added too; it was the one thing still dirtying `git status`. `git status --porcelain` on `main` is now empty.*
- [ ] **G0.5** 🧭 Doc fixes, docs only:
  - Drop `OpenCvSharp5.AvaloniaExtensions` from PLAN's package table and CLAUDE's stack table (V4).
  - Add the test and logging packages (V5).
  - Correct TESTING.md's hash-invariant paragraph (V6).
  - Propagate "golden hashes on Windows only; the macOS leg filters them out by trait" to CLAUDE.md, TESTING.md, stream-b and PLAN.md (V6).
  - Rewrite TESTING's and PLAN's skip-zero definition per V7.
  - Mark RECONCILIATION's "branches not merged" as done.

  Accept: `grep -rn "OpenCvSharp5.AvaloniaExtensions\|both CI legs" docs CLAUDE.md` finds only historical or record text.
- [x] **G0.6** 🧭 Hook fixes, with each case chaos-tested using synthetic payloads:
  - `guard-write.sh` resolves the git dir **of the target file's directory** (`git -C "$(dirname "$abs")" rev-parse …`), not `$PWD` (V3).
  - Exempt the Windows scratchpad path.
  - Add `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `*/Core/Fakes/*` and `*/Tests/Integration/*` to the frozen list.

  Accept: a write aimed at a worktree's `Core/Abstractions` while the session cwd is `main` is denied, and the same path in `main` is allowed.

  *Done 2026-09-21.* **33 synthetic payloads, target path and session cwd varied independently; all 33 pass, and the pre-fix hook gets 13 of them wrong** — the V3 hole itself (worktree target + `main` cwd allowed silently, with no output at all, for every one of the six frozen categories), its inverse (a worktree cwd froze `main`'s own contract files), and the Windows scratchpad denial. Also verified live through the real `Write` tool from `main`, which is the exact configuration every subagent runs in. Target directories that do not exist yet resolve by walking up to the nearest existing ancestor, so the guard holds before Stream 0 creates `src/`. Harness: `scratchpad/test-guard-write.sh` (not committed — it hardcodes absolute machine paths; the 33 cases are listed in the commit body).

### Stream 0 — Foundation (serial, on `main`, one implementer at a time)
Write scope for S0 is the whole repo, except for other streams' future directories.

- [ ] **S0.0** 👤 On the Mac: a throwaway console app on `net10.0` loads `OpenCvSharp4` and `runtime.osx.arm64`, calls `Cv2.GaussianBlur`, and prints `Cv2.GetBuildInformation()`. Record the **"Used HAL"** line here. Waivable per G0.3.
- [ ] **S0.1** Solution skeleton, the item most sensitive to the freeze:
  - `global.json` (SDK 10.0.x, `rollForward: latestFeature`).
  - `Directory.Build.props`: `net10.0`, `Nullable` enable, `ImplicitUsings`, `TreatWarningsAsErrors` for `src/`.
  - `Directory.Packages.props` (central package management) with **exact** pins:
    - Avalonia, `.Desktop`, `.Themes.Fluent`, `.Controls.DataGrid`, `.Headless.XUnit`: 12.1.2
    - `AvaloniaUI.DiagnosticsSupport`: 2.2.3
    - `CommunityToolkit.Mvvm`: 8.4.2
    - `OpenCvSharp4`, `.runtime.win`, `.runtime.osx.arm64`: 4.13.0.20260627
    - `FlashCap`: 1.12.0
    - `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Console`
    - `CsvHelper`
    - `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`
  - `LoreFetch.slnx`.
  - Projects `src/LoreFetch.Core`, `src/LoreFetch.Capture`, `src/LoreFetch.Lab` (console exe) and `src/LoreFetch.App` (Avalonia WinExe), plus `Tests/StreamA|B|C|D` and `Tests/Integration` (xunit v3).
  - Project references:
    - Capture, Lab and App → Core
    - App → Capture
    - Tests.StreamA → App and Core
    - Tests.StreamB → Core and Lab
    - Tests.StreamC → Capture
    - Tests.StreamD → Core
    - Tests.Integration → all four
  - **Over-reference:** every OpenCvSharp package goes into Core, Capture, Lab, StreamB, StreamC and Integration. `CsvHelper` goes into Core. Logging goes everywhere.
  - `App.csproj` copies `data/index/**` to the output and publish directories (V11). Create `data/index/.gitkeep`.
  - `InternalsVisibleTo` from Capture to Tests.StreamC, for C's internal stage.

  Accept: `dotnet restore` and `dotnet build -c Release` are clean. **If any pin fails on `net10.0`, stop and ask** about falling back to `net8.0`, which is PLAN's stated fallback.
- [ ] **S0.2** CI at `.github/workflows/ci.yml`: `windows-latest` and `macos-latest` each run restore, build and `dotnet test`. The macOS leg adds `--filter "Category!=WindowsOnly"`. Both legs add `Category!=Hardware`. Accept: the YAML lints, and it runs at the P1 push.
- [ ] **S0.3a** `Core/Abstractions`, types and interfaces, **verbatim from CONTRACTS.md**:
  - frames, `FrameGeometry`, `IFrameSource`, `IFrameSourceFactory`, `FrameSourceException`
  - `PointF2`, `CardQuad` (computed `AreaPx` and `AspectRatio`), `ICardDetector`, `RectifiedCard`, `IRectifier`
  - `CardCandidate` (with `ArtworkId`), `ICardIdentifier`, `OracleEntry`, `IOracleCatalog`
  - `IAutoCaptureTrigger`, `CollectionRow`, `RowSource`, `ICollectionStore`, `CollectionStoreException`, `ExportFormat`, `ICollectionExporter`
  - `ScanSettings`, whose rotation setter throws on anything other than 0, 90, 180 or 270

  `CameraFrame` returns its buffer to the pool exactly once, and `Dispose` is idempotent. There are no OpenCvSharp types anywhere in the contract surface. Accept: unit tests for `CameraFrame` (disposing twice returns the buffer once, counted with a test pool), for `CardQuad` maths, and for the `ScanSettings` rotation guard.
- [ ] **S0.3b** `Cohort`, `CohortTile` and `TileState` with public constructors (V10). Accept: every `CohortTile` case in TESTING.md §Unit passes, plus these:
  - the initial state follows the table at ≤good, ≤ok and >ok, and with no candidates
  - `ToggleExcluded` on `Unresolved` is a no-op
- [ ] **S0.4a** `Core/Scanning`: `DetectionSnapshot`, `IScanPipeline` and its implementation. Rules:
  - The pipeline retains exactly one frame, guarded by a lock.
  - `CaptureAsync` takes ownership of the frame under the lock, then does its work outside the lock.
  - Detection always uses `maxCards = 9` (V12).
  - `now` comes from `startWallClock + Stopwatch.Elapsed`.
  - `NotifyCaptured()` is called on every capture, manual or auto.
  - `SourceFailed` is raised before `RunAsync` faults.
  - Thresholds come from `ScanSettings` and are passed into each tile.
- [ ] **S0.4b** `ScanPipelineFactory.Create(...)`, plus the thresholds loader `ThresholdsFile.Load(path) → ThresholdsFile`. Schema v1 is JSON with these fields: `formatVersion`, `goodDistance`, `okDistance`, `referenceFloor`, `indexArtworkCount`, `indexSha256`, `measuredAt`, `notes`. Unknown `formatVersion`, a missing file or a missing field throws `InvalidDataException` or `FileNotFoundException`. Also `DataFiles.IndexPath` and `DataFiles.ThresholdsPath`, relative to `AppContext.BaseDirectory`. Accept: loader unit tests for a valid file, a missing file, a bad version and a missing field.
- [ ] **S0.5a** ∥ Fakes in `src/LoreFetch.Core/Fakes/`: `FolderFrameSource` plus `FolderFrameSourceFactory` (V20), `StubCardDetector` (1/3/9 layouts) and `StubRectifier`. Accept: unit tests. `FolderFrameSource` disposes every frame it drops.
- [ ] **S0.5b** ∥ `StubCardIdentifier` (configurable distances), `StubOracleCatalog` and the two collection stubs:
  - `StubOracleCatalog` defaults to 33,000 synthetic entries. It always includes the hostile names: `Kongming, "Sleeping Dragon"`, `"Rumors of My Death . . ."`, `Lim-Dûl's Vault`, `Borrowing 100,000 Arrows` and `+2 Mace`.
  - `StubCollectionStore` has real dedup semantics and a switch to throw on the next commit.
  - `StubCollectionExporter` takes a configurable `ExportFormat`.

  Accept: unit tests.
- [ ] **S0.6a** `Tests/Integration` harness:
  - an `IImplementationSet` provider with `Fakes` and a skipping `Real` stub (V8)
  - the `LOREFETCH_REQUIRE_REAL` switch (V7)
  - a frame generator writing to a temp folder (plain fills with drawn rectangles)
  - `ScriptedTrigger` (V9)

  Cases: an X'd tile is absent; Escape writes nothing; `ManuallySet` commits as `Source=Manual` with no distance; a cleared tile commits the machine's proposal; re-commit increments the quantity; a 7-of-9 partial cohort commits 7.
- [ ] **S0.6b** More `Tests/Integration` cases:
  - `CaptureAsync` rectifies the latest snapshot's frame, and is hammered concurrently with the loop
  - every pooled frame is disposed exactly once over a sustained run, using a counting pool
  - a thumbnail is still readable after commit
  - `SourceFailed` fires on a throwing source
  - `AutoCaptured` fires via `ScriptedTrigger`

  Accept for S0.6a and S0.6b: `dotnet test Tests/Integration` is green, and every `Real` skip gives its reason.
- [ ] **S0.7** Scaffolding:
  - one placeholder test per `Tests/StreamX` project, so each builds and runs
  - README: add a `## Stream A — UI` … `## Stream D — Collection & export` section skeleton, and keep the existing content
  - `THIRD-PARTY-NOTICES` untouched (entries are added at integration)
- [ ] **S0.8** 🧭 **Freeze review.**
  - Diff `Core/Abstractions` against CONTRACTS.md member by member.
  - Confirm the identity guard with a refused commit (`GIT_AUTHOR_EMAIL=someone@example.com`).
  - Run `dotnet test` on the whole solution.
  - Commit `Stream 0: foundation`.
- [ ] **P1** 👤 Push checkpoint. Show the summary and wait for approval, then push `main`. Both CI legs must be green, and a red leg sends the work back to S0.

### ⛩ G1 — Fork gate
Open only when G0.* and S0.1–S0.8 plus P1 are all ticked. S0.0 may be waived. Then create the four worktrees (§0). From here on, `Core/Abstractions`, `Core/Scanning`, `Core/Fakes`, `Tests/Integration`, every `.csproj`, the `.slnx`, the `Directory.*` files and `global.json` are **frozen**. A stop-and-ask from any stream is taken to the user, and a change that is approved lands on `main` and is then merged into every stream branch.

### Human track H (parallel, may start at G0)
- [ ] **H1** 👤 Print the adjustable camera mount. Lock the height at about 9.75″ with the 1920 axis along the table's depth.
- [ ] **H2** 👤 Lighting and mat:
  - put the SAD lamp off-axis at a shallow angle
  - check a blank frame for PWM banding
  - try light, mid and dark mats, and record which one detects best
- [ ] **H3** 👤 Capture the fixture corpus:
  - heights 8, 10, 12, 14 and 20″
  - 1, 3 and 9 layouts, some rotated
  - lands, normal cards and stretch cards
  - store them in the gitignored `test-images/fixtures/<height>in/<layout>/…jpg` (same relative path on the Mac and the PC; see `test-images/README.md`)
  - ground truth goes in `test-images/ground-truth.csv` with columns `file,height_in,layout,slot,oracle_name,rung,mat`
  - back it up outside git

  Gates B6.
- [ ] **H4** 👤 A Moxfield account. Gates D5.
- [ ] **H6** 👤 A C920 attached to this PC. Gates C4.

### Stream A — UI · worktree `stream-a` · scope `src/LoreFetch.App/**`, `src/LoreFetch.Core/Trigger/**`, `Tests/StreamA/**`, README §A
Global overrides for every A brief:
- Build against the fakes only.
- `StubOracleCatalog` holds ~33k entries.
- Never compare a distance. Render `State` and `IsLowConfidence`.
- Never set `IsDefault` on any button.
- Status text comes from `IScanPipeline.SourceDescription`.

- [ ] **A0** `AutoCaptureTrigger` in `Core/Trigger`:
  - count-gated settle over `SettleMilliseconds`
  - movement epsilon is `MovementTolerancePixels`, matched by **nearest centroid**
  - "the scene breaks" means any count ≠ expected
  - re-arm only after the scene breaks, and `NotifyCaptured` suppresses re-firing

  Accept: every case in TESTING.md §Unit passes, including two near-equal-area quads swapping order without resetting the settle.
- [ ] **A1** App shell and composition root:
  - `AppComposition` offers a `Fakes` mode, with `Real` added at integration
  - it calls `ScanPipelineFactory.Create` and `IFrameSourceFactory.CreateAsync`, then `RunAsync` once
  - a status line
  - it resolves `AttachDeveloperTools` licensing in its first hour, and leaves it uncalled if the tool turns out to be paid

  Accept: the app launches against `FolderFrameSourceFactory`.
- [ ] **A2** Preview:
  - one `WriteableBitmap` for the life of the app
  - lock, copy and dispose the lock every frame
  - BGR→BGRA conversion that switches on `Layout` and respects `RowBytes`
  - `InvalidateVisual()` on the `Image`
  - coalesce via `RequestAnimationFrame`, throttled to about 15 fps
  - copy inside the `FrameProcessed` handler

  Accept: unit tests for the pure `PixelConvert` function (BGR24/BGRA32 with padded strides).
- [ ] **A3** Quad overlay: vector children over the `Image`, and a pure frame→control transform that handles `Uniform` letterboxing. Accept: transform unit tests with rotated (1080×1920) geometry.
- [ ] **A4** ∥ Expected-count selector (1, 3 or 9) and an auto-capture toggle, both written through a view model to `ScanSettings`.
- [ ] **A5** Cohort grid: a tile view model wraps `CohortTile` and raises INPC. It has the four state visuals plus a low-confidence highlight. Accept: view-model unit tests for each state.
- [ ] **A6** Tile interactions: left-click calls `ToggleExcluded`, and the context menu offers `SetManually` and `Clear`. The type-ahead uses these `AutoCompleteBox` settings:
  - `AsyncPopulator`, filtering off the UI thread with `Take(20)`
  - `MinimumPrefixLength` of 2
  - `MinimumPopulateDelay` of 150 ms
  - ordinal matching
  - runners-up listed first

  Accept: a populator unit test against 33k entries (capped, cancellable).
- [ ] **A7** Keyboard map:
  - a window-level `Tunnel` handler, which returns without setting `Handled` when focus is in a `TextBox`
  - Space awaits `CaptureAsync`
  - Enter commits and keeps the cohort on `CollectionStoreException`
  - Escape discards
  - keys 1–9 are optional

  Accept: `Avalonia.Headless.XUnit` tests cover Space, Enter and Esc, and check that **a space typed into the type-ahead still arrives**.
- [ ] **A8** Collection view and export: a sortable `DataGrid` over `ListAsync` (keep the DataGrid theme include), and an exporter picker with an `IsVerified` badge. No per-format code: `grep -ri moxfield src/LoreFetch.App` must return nothing.
- [ ] **A9** Non-happy states:
  - empty collection
  - no frame source
  - `SourceFailed`
  - a store-lock retry
  - a missing index or thresholds file

  No stack traces in the UI.
- [ ] **A10** 🧭👤 Done-when run on this PC:
  - all three layouts work
  - a keyboard-only loop works
  - all four tile states are reached (two through configured distances, two through user action)
  - memory stays flat for 5 minutes under `dotnet-counters`

  Record the fps and memory results. Then README §A. **Merge gate, then P2.**

### Stream B — Identification (critical path) · worktree `stream-b` · scope `src/LoreFetch.Core/Identification/**`, `src/LoreFetch.Core/Imaging/**`, `src/LoreFetch.Lab/**`, `Tests/StreamB/**`, `data/index/**`, `docs/accuracy.md`, README §B
Global overrides for every B brief:
- Steps 2–3 run on the **reference side only**.
- Step 3 is `INTER_AREA` **by choice**.
- The warp is pinned to `INTER_LINEAR`.
- Assert on `CardCandidate.ArtworkId`, not on the concrete type.
- Results are top-N **distinct `OracleId`**.
- **No early rejection.**
- Pull `normal` images, never `small`.
- The Scryfall image cache lives **outside the repo**, at `C:\LoreFetchData\scryfall-cache`.
- Golden tests carry `[Trait("Category","WindowsOnly")]`.

- [ ] **B1a** Hash core in `Core/Imaging`:
  - `ReferenceTransform.Prepare`: GaussianBlur 3×3 with σX = σY = 1 given explicitly, then resize to 96 px wide `INTER_AREA`, then grayscale
  - `QueryTransform.Prepare`: grayscale only
  - the **single** shared `CardHasher.Hash(gray)` for steps 4–6: region `w×0.85w`, then 32×32 `INTER_AREA`, then per cell the **upper order statistic (33rd of 64)** with the tie-break `v == m && m > 128`, packed one `ulong` per cell
  - Hamming distance via `PopCount`

  Accept, as bounds rather than equalities: two input scales stay within a bound, brightness and gamma shifts stay within a bound, an inverted image lands at ≈1024, and the median and tie-break unit tests pass.
- [ ] **B1b** Golden hashes: deterministic procedural inputs, with the expected hex committed after it is generated **on win-x64**. Accept: the test passes on Windows and is filtered out on macOS.
- [ ] **B3a** Index format `cards.lfidx`:
  - header: magic, version, counts
  - per entry: 16 `ulong` hash values, an oracle index, the `ArtworkId`, and an `IsBasicLand` flag (V11)
  - an oracle name table
  - a reader and a writer

  Accept: round-trip tests on a synthetic index.
- [ ] **B3b** `HashCardIdentifier`, which implements `ICardIdentifier` and `IOracleCatalog`:
  - brute-force search
  - best distance per `OracleId`
  - hashes both 180° orientations and keeps the better one
  - never applies a threshold

  Accept: ranking and distinctness tests, plus a timing test for 9 queries against a synthetic 50k index (log the time; soft-fail above 50 ms).
- [ ] **B4a** ∥ Lab `bulk` command:
  - `GET /bulk-data/unique_artwork` with the required `User-Agent` and `Accept` headers
  - download `jsonl_download_uri` to `scryfall-bulk/` (gitignored)
  - apply the filter cascade from stream-b §B4 and print the count at each step

  Accept: a unit test over a small committed JSONL sample (text only) reproduces the filter decisions.
- [ ] **B4b** Lab `images` command: download `image_uris.normal` into the external cache. It resumes, runs with polite concurrency, respects HTTP 429, and skips files already present.
- [ ] **B4c** Lab `build-index` command: `ReferenceTransform` → `CardHasher` → `cards.lfidx`. It supports a `--subset N` fallback that labels the index size.
- [ ] **B4d** 🧭👤 Run B4a–B4c in full on **this win-x64 PC** (about 6 GB, hours). Commit `data/index/cards.lfidx` and record its artwork count and SHA-256 here. If it stalls, use a labelled 5k subset.
- [ ] **B5a** ∥ `ContourCardDetector` in `Core/Imaging`:
  - Canny → morphological close → `findContours` External → `approxPolyDP` to 4 points → order corners TL, TR, BR, BL
  - aspect 1.397 ±15% (the ±25% widening is configurable) and a minimum area
  - top N by area
  - every discard reason logged

  Accept, on generated frames: an empty mat on light, mid and dark backgrounds returns 0; a partial grid returns the correct count; a hand-shaped blob is rejected; rotated cards come back correctly ordered.
- [ ] **B5b** `PerspectiveRectifier`: warp to 488×680 with `INTER_LINEAR` pinned. Accept: a known quad on a synthetic frame produces the expected corner pixels.
- [ ] **B7** Lab synthetic generator:
  - px/inch = 1360/height
  - keystone, blur, noise and JPEG artefacts
  - a bare-mat generator
  - it **calls the shipping detect → rectify → hash path**
  - CI uses procedural card-like images and an index built inside the test (V13)

  Accept: an integration test in `Tests/StreamB` confirms that a generated card at 9.75″ retrieves its own artwork.
- [ ] **B2** Round-trip gate. It runs locally and is artifact-gated on the external cache and the committed index: Scryfall render → `RectifiedCard` → `Identify`. It asserts that rank 1 has the **same `ArtworkId`** and a distance ≤ the recorded floor bound. It writes `referenceFloor` and the margin to the best different artwork into `thresholds.json`. It uses a fixed sample, for example 200 renders spread across the ladder. **If the rank-1 artwork rate is below 99%, stop and ask.** Record the floor here.
- [ ] **B5c** Lab crop-scale experiment: measure the floor against crop scale. If the curve is sharp, add a 3-scale sweep inside the identifier. Record the decision here.
- [ ] **B6** Accuracy harness, gated on H3 and B4d:
  - correct@1, wrong@1 and no-match for each height and rung
  - the margin distribution
  - lands excluded by the `IsBasicLand` flag, with the excluded count printed
  - the three buckets sum to 100%
  - the run fails if wrong@1 exceeds the stated N at `OkDistance`
  - it writes `goodDistance` and `okDistance` into `thresholds.json` and the table into `docs/accuracy.md`

  **If correct@1 on normal cards is below 90% at the chosen height, stop and ask** (stream-b Fallbacks). Record the results here.
- [ ] **B8** 🧭 Done-when review against stream-b §Done when. Then README §B. **Merge gate, then P3.**

### Stream C — Capture · worktree `stream-c` · scope `src/LoreFetch.Capture/**`, `Tests/StreamC/**`, README §C
Global overrides for every C brief:
- The FlashCap shim is the only code that cannot be tested. Everything else goes behind an internal stage fed with `byte[]` JPEGs.
- Use `ArrayPool<byte>.Shared`, never `Create()`.
- The channel has capacity 1, `DropOldest`, and an `itemDropped` callback that disposes the dropped frame.
- Drop frames before decoding them.
- No test ever opens a device outside the `Hardware` trait.

- [ ] **C1a** Internal stage, channel and pooling: newest-frame-only, disposal on drop, and `InvalidOperationException` on a second enumerator. Accept: under a slow consumer, memory stays bounded and every buffer is returned (counting pool).
- [ ] **C1b** Decode and rotate: `Cv2.ImDecode` into a pooled BGR24 `CameraFrame`, then `Cv2.Rotate` using the rotation read once at open. `Geometry` is post-rotation. Decode time is logged. Accept: tests on synthetic JPEGs (encoded in memory) at 0, 90, 180 and 270 degrees.
- [ ] **C1c** Watchdogs: `FirstFrameTimeoutMs` and `FrameWatchdogMs` raise `FrameSourceException` naming the three causes. Accept: tests with a fake clock or short timeouts.
- [ ] **C2** FlashCap shim and `WebcamFrameSourceFactory : IFrameSourceFactory`:
  - enumerate and log every descriptor with its backend
  - zero descriptors gets its own diagnosis
  - select 1920×1080, `PixelFormats.JPEG`, `(double)fps >= 30`, or throw with the full list
  - backend preference DShow, then MF, never VfW
  - a backend-prefixed `PreferredDeviceId`
  - `Description` reports what was negotiated

  Accept: unit tests of the selection logic over plain descriptor data objects.
- [ ] **C3** ∥ README §C (including the macOS compile-only note).
- [ ] **C4** 👤🧭 Hardware run on this PC, gated on H6. The log shows 1080p MJPG at 30 fps. Memory stays flat over several minutes. A slow consumer causes latency, not growth. An unplug gives a clean error and a replug restarts. Decode time is recorded here. **Merge gate, then P4.**

### Stream D — Collection & export · worktree `stream-d` · scope `src/LoreFetch.Core/Collection/**`, `src/LoreFetch.Core/Export/**`, `Tests/StreamD/**`, README §D
Global overrides for every D brief:
- v1 is **native + Moxfield**.
- `null` is the only representation of "unassessed".
- Writing uses a temp file in the **same directory**, then `Flush(true)`, then `File.Move(overwrite:true)`, then a `.bak` of the previous file.
- The native format has a BOM. Moxfield has none.
- Never sanitise `+2 Mace`.
- Duplicate rows merge on read, justified by import robustness.

- [ ] **D1** Native CSV codec:
  - an RFC 4180 writer that quotes on `,`, `"`, CR and LF, and whenever a field contains a quote
  - a parser that rejects whitespace before an opening quote as a quote, treating it as content
  - an exact header check, which throws on any unknown or missing column
  - blank ↔ `null` for Condition
  - invariant culture, with ISO-8601 round-trip (`"o"`) timestamps
  - duplicate merge with a log line

  Accept: all 5 vectors from stream-d §D3 round-trip; the BOM is not taken as data; an empty collection produces a header-only file.
- [ ] **D2** `NativeCsvExporter`: the shared codec, `leaveOpen: true`, and a BOM. Accept: the caller's stream is still usable after export.
- [ ] **D3** `CsvCollectionStore`:
  - commits `Included` and `ManuallySet` tiles, folding duplicates within a cohort
  - returns the number of **cards** committed
  - throws on a null `Chosen`
  - uses the write sequence above
  - raises `CollectionStoreException` on a locked file
  - `ListAsync` on a missing file returns an empty list

  Accept: the blank-condition round trip yields 1 row with quantity 2; nine Forests return 9; a file locked with `FileShare.None` throws and leaves the original byte-identical; the temp file lives in the target directory.
- [ ] **D4** `MoxfieldCsvExporter`: the header per stream-d §D2 (`Count`, `Name`, printing columns blank), no BOM, `IsVerified = false`. Accept: tests for the header and the 5 vectors.
- [ ] **D5** 👤 Real import into Moxfield (gated on H4). Record the tool, date, row count, and what printings and blank conditions resolved to in README §D. Then set `IsVerified = true`.
- [ ] **D6** README §D: the unsupported-tools table and the `+2 Mace` Excel note. **Merge gate, then P5.**

### Integration I (on `main`, after the relevant streams have merged)
- [ ] **I1** After D merges: implement `Real` in `Tests/Integration` for the store and exporters, and switch `AppComposition` to use them.
- [ ] **I2** After A and B merge: `Real` gets the detector, rectifier, identifier and catalog, loaded from `DataFiles`. B7's synthetic frames take the `FolderFrameSource` slot. `AppComposition` gets a `Real` mode that loads the index and thresholds.
- [ ] **I3** After C merges: `WebcamFrameSourceFactory` goes into `AppComposition`, with a switch between the demo folder and the camera.
- [ ] **I4** 🧭 Grep for any hardcoded distance outside `Core/Scanning`, `CohortTile` and `thresholds.json`. There must be none.
- [ ] **I5** `THIRD-PARTY-NOTICES`: an entry for every package, so the hook's advisory list is empty. Chase down the FFmpeg notices in the OpenCvSharp runtimes (PLAN risk 7).
- [ ] **I6** 🧭 `LOREFETCH_REQUIRE_REAL=1 dotnet test` on this PC with every artifact present gives **0 skipped**. Then P6.

### Endgame E
- [ ] **E1** 👤🧭 Live calibration at the locked height, all three layouts, the full ladder. Record "predicted X, measured Y" and the difference in `docs/accuracy.md`.
- [ ] **E2** Polish:
  - README final pass: install, known limitations, SmartScreen
  - publish with `-r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
  - check it on a clean Windows profile with no .NET installed: no loose native DLLs, and `data/index` ships alongside
- [ ] **P7** 👤 Approve the `v0.1.0` tag and release.
- [ ] **E3** 👤 The 5-minute video (PLAN §E3). **If E1 overruns, cut E2, never E3.**

## 3. Push checkpoints

Every checkpoint means: show the summary, get explicit approval, push, and see both CI legs green.

| Checkpoint | When |
|---|---|
| P1 | End of Stream 0 |
| P2 | Merge of stream A |
| P3 | Merge of stream B |
| P4 | Merge of stream C |
| P5 | Merge of stream D |
| P6 | Integration done |
| P7 | Release |

The merge checkpoints P2–P5 happen in whatever order the streams finish.

## 4. Concrete names fixed for integration (V8, V19)

These are the names streams must use so that integration wiring is mechanical. They are not frozen contract; renaming one is a cheap edit at integration.

| Stream | Type | Construction |
|---|---|---|
| B | `LoreFetch.Core.Identification.HashCardIdentifier` : `ICardIdentifier`, `IOracleCatalog` | `static HashCardIdentifier Load(string indexPath, ILoggerFactory)` |
| B | `LoreFetch.Core.Imaging.ContourCardDetector` : `ICardDetector` | `(ILogger<ContourCardDetector>)` |
| B | `LoreFetch.Core.Imaging.PerspectiveRectifier` : `IRectifier` | `()` |
| C | `LoreFetch.Capture.WebcamFrameSourceFactory` : `IFrameSourceFactory` | `(ILoggerFactory)` |
| D | `LoreFetch.Core.Collection.CsvCollectionStore` : `ICollectionStore` | `(string path, ILogger<CsvCollectionStore>)` |
| D | `LoreFetch.Core.Export.NativeCsvExporter`, `MoxfieldCsvExporter` : `ICollectionExporter` | `()` |

