# Orchestration plan

The execution control document for the build. It tells the orchestrating agent what to do, in what order, and when it may proceed. **Decisions live in [`../CLAUDE.md`](../CLAUDE.md), contracts in [`CONTRACTS.md`](CONTRACTS.md), and stream detail in the stream docs.** This file sequences and slices that material. It does not restate it, except where a Sonnet implementer would otherwise act on stale text.

> **User decisions recorded here (2026-09-21):**
> - The golden-hash test runs on **Windows only**. The macOS leg filters it out by trait, because `INTER_AREA` is not bit-exact on ARM64. This supersedes "both CI legs" wherever it appears. *Propagated by G0.5.*
> - **Merge gate:** each stream merges once `dotnet build` and `dotnet test` are green locally. After that come fixed push checkpoints (§3), and each one needs the user's approval. Both CI legs must be green at every push.
> - **The orchestrator runs on the Mac, not the Windows PC.** See *Platform switch* below.

### Platform switch — recorded 2026-09-21, after G0.6

This file was written assuming the orchestrator sat at `C:\Repos\LoreFetch` on the Windows PC. **It does not: it runs in the main checkout on the Apple Silicon Mac,** `/Users/jnapoli/Repos/LoreFetch`. The user's ruling: build and test here, **commit and push**, and the user validates after each push, with parallel work continuing while a validation is outstanding.

Read every remaining **"this PC"** in this file as **the Mac**, except in the items below, which are win-x64 by their nature and become 👤 asks routed to the Windows PC:

| Item | Why it stays on win-x64 |
|---|---|
| **B4d** | The index must be built and committed on the ship target — `INTER_AREA` is not bit-exact on ARM64, so a Mac-built index would not match Windows queries. Non-negotiable. |
| **B1b** | Goldens are generated on `win-x64` for the same reason, and traited `WindowsOnly`. |
| **C4** | The C920 is on the PC. **Confirmed attached (G0.3/H6).** |
| **A10** | "All three layouts, keyboard-only, flat memory" is the shipping platform's run. A Mac pass is useful but not the criterion. |
| **I6** | `LOREFETCH_REQUIRE_REAL=1` with every artifact present, which includes the committed index and the Scryfall cache. |
| **E1, E2** | Live calibration and the single-file publish check. |

Four consequences that are not just bookkeeping:

1. **`global.json` pins `10.0.201`, the Mac's SDK — not the PC's `10.0.401`.** `rollForward: latestFeature` rolls a lower feature band *up* and never down, so pinning 10.0.401 would make the Mac unable to build at all, while pinning 10.0.201 satisfies both machines. S0.1 carries this as an override.
2. **Risk 7 is promoted from dev-only to primary.** `OpenCvSharp4.runtime.osx.arm64` has exactly one release, and PLAN's stated worst case — *"local CV testing is lost and we lean on the PC beside us"* — was written when the Mac was the secondary machine. It is now where every build and test runs, so that package failing blocks the build rather than costing convenience. This is why **S0.0 runs first, before S0.1**, exactly as PLAN item 0 insists.
3. **S0.0 is no longer 👤.** It was marked so only because the orchestrator was assumed to be on the PC and a human would have to walk to the Mac. The orchestrator is on the Mac, so it runs S0.0 itself (🧭). G0.3's Mac-access question is answered by construction.
4. **Stream B's Scryfall cache path** is Windows-shaped (`C:\LoreFetchData\scryfall-cache`). B4b/B4c/B2 run on whichever machine holds the cache; the path becomes a parameter rather than a constant, and B4d's run uses the Windows one.

### Contract change — `ArtworkId` to the collection file, ruled 2026-09-21 before the fork

A proposal raised after Stream 0's contract work: identification already produces the matched art's Scryfall printing id (`CardCandidate.ArtworkId`, also stored per entry in `cards.lfidx`) and then **discards it at the tile**, so the collection file — the stated source of truth that "carries everything we have at commit time" — never sees it. Accepted with two amendments. Full ruling and reasoning in [`RECONCILIATION.md`](RECONCILIATION.md).

**Applied on `main` before any worktree existed**, which is the only moment it is cheap: after the fork it stalls four streams, and every row scanned in the meantime has lost its art irrecoverably — rescanning the physical card is the only way back.

| Package | Amendment |
|---|---|
| **S0.3a** | `CollectionRow` gains `string? ArtworkId`, **appended last** — it is a positional record struct, so a mid-list parameter would silently break every positional construction site. `ICollectionStore`'s doc comment gains the fold rule. |
| **S0.3b** | `CohortTile` gains `ChosenArtworkId`, with the same null rules as `ChosenDistance` (null when `ManuallySet` — a manual pick names a card, not an art — and null when `Unresolved`). `ProposeFromHash` widens to carry it, so **V10 still holds: one function computes the proposal**. |
| **S0.5b** | `StubCardIdentifier` must emit **synthetic art ids**, with a mode making two candidates for one oracle card disagree so the fold is testable, plus a null mode so "null for stubs" stays covered. It previously returned null unconditionally, which would have left the new column dead on every fakes path — all of Stream 0 and all of stream A — and broken on stream B's first real population. `StubCollectionStore` implements the mapping and the fold. |
| **S0.6a/S0.6b** | New cases: the tile carries the id; `SetManually` nulls it; `Clear` restores it; commit sets it on `Hash` rows; agreeing arts keep it; **disagreeing arts null it**; `Manual` rows null. |
| **D1, D3** | The header set changed — the exact-header check and format-version expectation must include it. See the D1 override below. |
| **B4a** | Gains the deferred measurement as an open question. |
| **D4** | **Unchanged.** Moxfield ignores the column in v1. |

**The fold rule is agree-or-null, never last-write-wins.** A non-null `ArtworkId` must be trustworthy; a value that is right sometimes and wrong sometimes, with nothing able to tell which, is worse than null for a field whose purpose is price resolution. Same reasoning as *Condition is blank in v1*.

## 0. How the orchestrator uses this file

**Roles**
- **Orchestrator.** Runs one long-lived session on `main` in the main checkout, `/Users/jnapoli/Repos/LoreFetch` (see *Platform switch*). It owns this file, dispatches packages, verifies them, merges, and ticks checkboxes. It never implements a package itself unless the package is marked 🧭 (orchestrator-only).
- **Implementer.** One Sonnet subagent per package. It works only in its stream's worktree and returns a report. Implementers never push, merge, or edit this file.
- **Human.** Packages marked 👤 need the user: hardware, **the Windows PC**, accounts, approving a push, recording the video. The orchestrator asks and waits. It never simulates them.

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
weakening a test.
Mention in your report, but do NOT stop or cut scope for: production code going past ~400 lines.
That number is a scope-drift tripwire, not a budget — it asks "is this package doing more than one
package's job?", and well-factored code with house-style doc comments passes it honestly. Never
delete a class, a guard, a doc comment or a test to get under it. Finish the package as specified.
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
- Local `dotnet build` and `dotnet test` are green on the Mac.
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
- [x] **G0.3** 👤 Confirm Mac access for S0.0 and an attached C920 for H6. If no Mac is available, record that S0.0 is waived and that Risk 7 is accepted. — *done 2026-09-21: Mac access is answered by construction, because the orchestrator now runs on the Mac (see Platform switch) — S0.0 becomes 🧭 and runs first. **C920 confirmed attached to the Windows PC**, so H6 is satisfied and C4 can run as written.*
- [x] **G0.4** 🧭 `.gitignore`: add `.claude/worktrees/`, `.idea/` and `.claude/skills/` (or commit skills deliberately), and fix the size comment. Accept: `git status` on `main` is clean. — *Partly done 2026-09-21: `.claude/worktrees/`, `.idea/` and the size comment are fixed, and `test-images/` was added as the local imagery folder. Still open: decide whether to ignore or commit `.claude/skills/`, then confirm on `main` after the merge.*
  *Finished 2026-09-21: `.DS_Store` added; it was the one thing still dirtying `git status`. `git status --porcelain` on `main` is now empty.*

  ***`.claude/skills/` is COMMITTED, not ignored** — corrected 2026-09-21 after the first call went the other way.* The first ruling was that skills are the operator's own tooling; that was wrong on the facts. **Claude Code runs on the Mac *and* on the Windows PC against this same repo, and in four linked worktrees after the fork.** `hooks/`, `settings.json` and `skills/` only apply where they exist on disk, so git is the only mechanism that puts them on the other machine — which is exactly the argument already used to keep the first two tracked. Not applying it to the third was the inconsistency. A skill that lives in one checkout is a skill the other machine silently does not have, and silence is the failure mode this project keeps designing against.

  The standing consequence: **this repo is public**, so `.claude/skills/` carries project skills only — nothing personal, nothing work-related. That is a content rule, not a mechanism, so it is written in `.gitignore` next to the decision rather than enforced. (Committing it also retires the dead-negation bug: `.claude/skills/*` plus `!…/lorefetch-run/` is gone, so there is no longer a negation to get wrong.)
- [x] **G0.5** 🧭 Doc fixes, docs only:
  - Drop `OpenCvSharp5.AvaloniaExtensions` from PLAN's package table and CLAUDE's stack table (V4).
  - Add the test and logging packages (V5).
  - Correct TESTING.md's hash-invariant paragraph (V6).
  - Propagate "golden hashes on Windows only; the macOS leg filters them out by trait" to CLAUDE.md, TESTING.md, stream-b and PLAN.md (V6).
  - Rewrite TESTING's and PLAN's skip-zero definition per V7.
  - Mark RECONCILIATION's "branches not merged" as done.

  Accept: `grep -rn "OpenCvSharp5.AvaloniaExtensions\|both CI legs" docs CLAUDE.md` finds only historical or record text.

  *Done 2026-09-21.* The grep's surviving hits are all deliberate: CLAUDE's stack table keeps `OpenCvSharp5.AvaloniaExtensions` as a **do-not-use** entry (dropping the warning would invite it back); stream-a's research bullet now states the 12.1.x pin on its own footing and notes the package it used to hang off is gone; RECONCILIATION keeps both original decisions with an inline **Superseded 2026-09-21** note rather than being rewritten, because it is the decision record; and "both CI legs must be green at every push" plus TESTING's merge-gate row are about the *build*, which does run on both legs. Also folded in, as the same class of stale text: CLAUDE.md's frozen-surface list in the header and the standing rules named only `Abstractions`, `Scanning` and `.csproj`, understating what G1 and `guard-write.sh` actually freeze.
- [x] **G0.6** 🧭 Hook fixes, with each case chaos-tested using synthetic payloads:
  - `guard-write.sh` resolves the git dir **of the target file's directory** (`git -C "$(dirname "$abs")" rev-parse …`), not `$PWD` (V3).
  - Exempt the Windows scratchpad path.
  - Add `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `*/Core/Fakes/*` and `*/Tests/Integration/*` to the frozen list.

  Accept: a write aimed at a worktree's `Core/Abstractions` while the session cwd is `main` is denied, and the same path in `main` is allowed.

  *Done 2026-09-21.* **33 synthetic payloads, target path and session cwd varied independently; all 33 pass, and the pre-fix hook gets 13 of them wrong** — the V3 hole itself (worktree target + `main` cwd allowed silently, with no output at all, for every one of the six frozen categories), its inverse (a worktree cwd froze `main`'s own contract files), and the Windows scratchpad denial. Also verified live through the real `Write` tool from `main`, which is the exact configuration every subagent runs in. Target directories that do not exist yet resolve by walking up to the nearest existing ancestor, so the guard holds before Stream 0 creates `src/`. Harness: `scratchpad/test-guard-write.sh` (not committed — it hardcodes absolute machine paths; the 33 cases are listed in the commit body).

### Stream 0 — Foundation (serial, on `main`, one implementer at a time)
Write scope for S0 is the whole repo, except for other streams' future directories.

- [x] **S0.0** 🧭 On the Mac: a throwaway console app on `net10.0` loads `OpenCvSharp4` and `runtime.osx.arm64`, calls `Cv2.GaussianBlur`, and prints `Cv2.GetBuildInformation()`. Record the **"Used HAL"** line here. ~~Waivable per G0.3~~ — **no longer waivable.** The Mac is where every build and test now runs, so this package failing blocks the build instead of costing local convenience (Risk 7, promoted). Built outside the repo tree so it never becomes project state.

  *Done 2026-09-21. **Risk 7 clears.*** `net10.0` restored against `OpenCvSharp4` + `OpenCvSharp4.runtime.osx.arm64` `4.13.0.20260627` in 2.9 s, ran on .NET 10.0.5 / `osx-arm64` / Arm64. `GaussianBlur` 3×3 with σX = σY = 1 passed explicitly returned a symmetric kernel (centre 52, edge 32, corner 19), and `INTER_AREA` 480→96 ran clean. Build info:

  | | |
  |---|---|
  | OpenCV | 4.13.0, vcs `fe38fc6`, contrib `d99ad2a`, built 2026-06-27T02:25:09Z |
  | Host | Darwin 25.4.0 arm64, CMake 4.3.4 |
  | **Custom HAL** | **`YES (carotene (ver 0.0.1) KleidiCV (ver 0.7.0))`** |
  | CPU baseline | `NEON FP16 NEON_DOTPROD NEON_FP16`; dispatched `NEON_BF16` |
  | Parallel framework | GCD |
  | 3rdparty | `tegra_hal kleidicv_hal kleidicv kleidicv_thread` … |

  **The "Used HAL" line the item asks for does not exist in 4.13's output; `Custom HAL` is its successor, and the answer it gives is the one that mattered.** `carotene` is *precisely* the NEON HAL that CLAUDE.md's Risk 2 and OpenCV #24163 name as the reason `INTER_AREA` is not bit-exact on ARM64 — so this macOS build routes through it, and the ruling that golden hashes are `win-x64`-only is now **measured rather than inferred**. It also means a Mac-built index could never match Windows queries, which is why B4d stays on the PC.

  Worth doing at B1b: have the Windows golden generator print the same procedural fixture's hash, and record the ARM64 value beside it. If they differ, the number is the concrete size of this divergence; if they agree, `INTER_AREA` is not being reached by carotene for our specific call and the trait could later be relaxed. Either answer is cheap and currently unknown.
- [x] **S0.1** Solution skeleton, the item most sensitive to the freeze:
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

  **Overrides, all measured on this Mac 2026-09-21 — take them as given rather than rediscovering them:**
  - **`global.json` pins `10.0.201`, not the PC's `10.0.401`.** `rollForward: latestFeature` rolls a lower feature band up, never down. Proven to restore and build on both `net10.0` probe projects.
  - **`xunit.runner.visualstudio` is not optional and not insurance.** With `xunit.v3` + `Microsoft.NET.Test.Sdk` but *without* it, `dotnet test` discovered **0 of 3 tests, printed "No test is available", and exited 0**. Every one of the five test projects needs it, or the whole suite silently stops testing and every signal stays green. Versions proven together: `xunit.v3` **3.2.2**, `xunit.runner.visualstudio` **3.1.5**, `Microsoft.NET.Test.Sdk` **18.0.0**.
  - **Do not set `<OutputType>Exe</OutputType>` on the test projects.** `xunit.v3` already sets it (verified with `dotnet msbuild -getProperty:OutputType`), so V18's "test projects are executables" needs no action.
  - **`.slnx` works** with SDK 10.0.201 for `restore`, `build` and `test`. No `.sln` fallback needed.
  - `OpenCvSharp4` + `OpenCvSharp4.runtime.osx.arm64` `4.13.0.20260627` both exist and restore on `net10.0` (S0.0).

  *Done 2026-09-21 (`4f1f765`), verified independently by the orchestrator rather than on the implementer's report.* **Every unproven pin restored on `net10.0` first time — Avalonia 12.1.2 (all five packages), `AvaloniaUI.DiagnosticsSupport` 2.2.3, `CommunityToolkit.Mvvm` 8.4.2, `FlashCap` 1.12.0 — so the `net8.0` fallback is not needed and PLAN's warning is discharged.** Versions resolved for the four that were unpinned: `Microsoft.Extensions.Logging{,.Abstractions,.Console}` **10.0.12** (11.x is prerelease only) and `CsvHelper` **33.1.0** (dual `MS-PL OR Apache-2.0`; Apache-2.0 elected, per the Approved License List).

  Verification performed: 19 files, every path in scope; commit authored and committed as `jnapoli87 <…users.noreply.github.com>` through the pre-commit hook; `bin`/`obj` wiped, then `restore` + `build -c Release` gave **0 warnings, 0 errors**; `TreatWarningsAsErrors` measured `true` for `src/*` and `false` for `Tests/*` via `dotnet msbuild -getProperty`; test projects' `OutputType` is `Exe` without being hand-set; `data/index/**` lands at `data/index/` in **both** `bin/Release/net10.0/` and a `dotnet publish` output, with the folder shape intact — worth checking because the `Link` metadata uses backslashes and a literal `data\index\.gitkeep` file was the plausible failure on macOS; no `.slim` or `OpenCvSharp5` anywhere; `InternalsVisibleTo` Capture → `LoreFetch.Tests.StreamC`; the DataGrid theme include is in `App.axaml`. `src/Directory.Build.props` imports the root one and then tightens it, which was the less clever of the two options offered.

  ⚠ **`scripts/lorefetch.sh run` hangs silently until stream A's A1 lands.** The App shell is deliberately bare — no `MainWindow`, no `OnFrameworkInitializationCompleted` override — so `StartWithClassicDesktopLifetime` enters its loop with no window and no output, indefinitely. Measured, not assumed. Not a defect to fix here: A1 owns the app shell and replaces that file. **Until then validate with `doctor` and `build` only**, and expect `test` to fail (zero tests until S0.7).

### `guard-bash.sh` false positive — fixed 2026-09-21

Found by hitting it: the early push below was **refused as a force-push**, because the same shell line ended with an `rm -f` of the commit-message temp file. The deny searched the whole command for a force flag rather than the `git push` segment, so any unrelated `-f` blocked a legitimate push. CLAUDE.md's recorded reasoning — that a loose push match could only ever warn, since the deny also required a force flag — was wrong: both halves were loose, and two loose tests do not conjoin into a tight one. Fixed so the force flag must follow `push` within the same shell segment. 33 synthetic cases: all 12 real force-push forms still denied, the 5 false positives gone, and the pre-fix hook fails exactly those 5.

A second consequence is permanent rather than fixable: **the guard reads command text, so it cannot distinguish a command from prose that contains one.** Authoring documentation *about* force-pushing through a Bash heredoc trips the deny — which happened while writing this very section. Use Write/Edit for that content. Detail in CLAUDE.md.

### Early push — 2026-09-21, before P1

The user called a push at the end of S0.1 rather than waiting for P1, so the Windows PC can start its own pull/build/validate loop while Stream 0 continues. `2af13a2..0a61aff`, 8 commits, 30 files, no imagery, every commit authored `jnapoli87 <…users.noreply.github.com>`.

**This is not P1 and does not tick it.** P1 still means the end of Stream 0. Two things P1 requires that this push could not satisfy: `.github/workflows/ci.yml` does not exist yet (S0.2), so **no CI leg ran at all** — "both CI legs green" is vacuous here rather than met; and Stream 0 is unfinished, so this is a coherent checkpoint, not a foundation. What the PC can verify today is `doctor` and `build`.
- [x] **S0.2** CI at `.github/workflows/ci.yml`: `windows-latest` and `macos-latest` each run restore, build and `dotnet test`. The macOS leg adds `--filter "Category!=WindowsOnly"`. Both legs add `Category!=Hardware`. Accept: the YAML lints, and it runs at the P1 push.

  **Overrides — this one changed shape after S0.1, read it carefully:**

  **CI must invoke `bash scripts/lorefetch.sh test`, not `dotnet test`.** Measured on 2026-09-21 against the real solution: `dotnet test` **exits 0 while running zero tests**, in *two* distinct ways — `"No test is available"` when no adapter is registered, and `"No test matches the given testcase filter"` when the filter excludes everything or the projects have no tests yet. Both were reproduced here and both returned exit code **0**. A CI leg calling `dotnet test` directly is therefore green when it has tested nothing, which is the exact failure the whole five-level test strategy is built to prevent. `scripts/lorefetch.sh` traps both and exits 1. Putting the guard only in the local script would leave it absent from the thing that actually gates merges.

  `bash` works on both runners — `windows-latest` ships Git Bash and `shell: bash` is supported there. Mirroring `default_filter()` in YAML as well would be a second source of truth for the filter; calling the script makes it one. Both legs still need `Category!=Hardware`; macOS adds `Category!=WindowsOnly`, and the script already derives that from `uname`, so the YAML passes no filter at all.

  The combined filter `Category!=Hardware&Category!=WindowsOnly` is proven correct (3 tests in, 1 out, traited pair excluded). If it is ever written in YAML anyway, **quote it** — `&` is a shell metacharacter and an unquoted filter backgrounds the command.

  ~~Expect this leg to be **red until S0.7** adds the placeholder tests, because five test projects with zero tests is itself a zero-test run.~~ **No longer true, and the reason is a defect that was in the guard rather than in the plan** (found 2026-09-21 when the user asked whether there were enough tests to validate on the PC):

  The guard hard-failed on `"No test matches the given testcase filter"` from *any* project. Once `Tests/Integration` had 52 passing tests and `Tests/StreamA|B|C|D` were still empty, the script reported **failure on a run where 52 tests passed** — a false negative that would have trained everyone to ignore it, which is worse than having no guard. A second bug hid behind it: the "did anything run?" check was anchored with `^(Passed!|Failed!)` while `dotnet test` **indents** its summary lines, so that check could never have fired correctly either.

  Now: a missing adapter (`"No test is available"`) stays a hard failure, because that is a misconfigured project silently testing nothing. A filter matching nothing in *some* projects is a **note** listing them — "expected while a stream is unstarted, suspicious once it is done" — and only the *whole run* matching nothing fails. Four cases verified: real tests plus empty projects passes with the note; nothing-anywhere fails; a missing adapter fails; real test failures fail.
> **Where Stream 0's own unit tests go.** S0.3a, S0.3b, S0.4b, S0.5a and S0.5b all say *"Accept: unit tests"*, but Stream 0 owns exactly one test project: **`Tests/Integration`** (CONTRACTS.md §Stream boundaries — *"Owned by Stream 0, not edited by streams: the fakes, `Tests/Integration/**`"*; TESTING.md tags the `CohortTile` unit cases *"(Stream 0)"*). So they go there, in their own files, alongside the end-to-end suite. They must **not** go in `Tests/StreamA|B|C|D`, which the streams own and may edit — a stream could then weaken or delete a foundation test. The project name is a wart, and a sixth `Tests/Foundation` project was considered and rejected: V18 fixed the count at five and S0.1 has already created and proven them, so adding one now is a plan change for a naming preference. Revisit before G1 if the wart is worth a project.

- [x] **S0.3a** `Core/Abstractions`, types and interfaces, **verbatim from CONTRACTS.md**:
  - frames, `FrameGeometry`, `IFrameSource`, `IFrameSourceFactory`, `FrameSourceException`
  - `PointF2`, `CardQuad` (computed `AreaPx` and `AspectRatio`), `ICardDetector`, `RectifiedCard`, `IRectifier`
  - `CardCandidate` (with `ArtworkId`), `ICardIdentifier`, `OracleEntry`, `IOracleCatalog`
  - `IAutoCaptureTrigger`, `CollectionRow`, `RowSource`, `ICollectionStore`, `CollectionStoreException`, `ExportFormat`, `ICollectionExporter`
  - `ScanSettings`, whose rotation setter throws on anything other than 0, 90, 180 or 270

  `CameraFrame` returns its buffer to the pool exactly once, and `Dispose` is idempotent. There are no OpenCvSharp types anywhere in the contract surface. Accept: unit tests for `CameraFrame` (disposing twice returns the buffer once, counted with a test pool), for `CardQuad` maths, and for the `ScanSettings` rotation guard.

  *Done 2026-09-21 (`aad179c`), verified independently.* 6 source files + 3 test files, every path in scope, faithful to CONTRACTS.md member by member with the doc comments carried across. Clean Release build at 0 warnings; **`grep -rn OpenCvSharp` over the directory is empty**, so the no-CV-on-the-seam invariant holds. `Passed! Failed: 0, Passed: 14, Total: 14`.
  - Dispose-once uses `Interlocked.Exchange(ref _buffer, null)` rather than a bool, so the pool return survives re-entry — stronger than the contract's "not thread-safe" floor, at no cost.
  - `Pixels` is sliced to `Stride*Height` in the constructor, once, so no reader can see the pool's round-up.
  - `AspectRatio` averages opposite sides then divides long/short, so it reads ~1.397 in either orientation.
  - The three chaos tests were reported, and **the orchestrator re-ran the dispose-once one independently** rather than trusting the report: with `Interlocked` removed the test fails `Expected: 1, Actual: 3` — three `Dispose()` calls, three pool returns, which is exactly the double-return bug and not an incidental failure. Reverted; 14/14 green again.

  ⚠ **Plan defect found here, not an implementer error: `ICollectionStore` was listed under S0.3a but cannot compile until S0.3b.** Its only cohort member is `CommitCohortAsync(Cohort, …)`, and `Cohort` is S0.3b's. So S0.3a was never completable as written — the split ran through the middle of a dependency. The implementer correctly stopped, left the rest building clean, and recorded a NOTE rather than inventing a stand-in `Cohort`, which would have been a unilateral edit to a surface about to freeze. **Resolution: `ICollectionStore` moves to S0.3b**, which also deletes that NOTE. Nothing else in S0.3a was affected.
- [x] **S0.3b** `Cohort`, `CohortTile`, `TileState` and `CaptureReason` with public constructors (V10), **plus `ICollectionStore`, moved here from S0.3a** — see the defect note above; it cannot compile before `Cohort` exists, and S0.3b also deletes S0.3a's deferral NOTE from `Collection.cs`. Accept: every `CohortTile` case in TESTING.md §Unit passes, plus these:
  - the initial state follows the table at ≤good, ≤ok and >ok, and with no candidates — asserting `State`, `Chosen`, `ChosenDistance` **and** `IsLowConfidence` each time, at the exact boundary values too, since the table is written with ≤
  - `ToggleExcluded` on `Unresolved` is a no-op
  - `ToggleExcluded` from `ManuallySet` round-trips back to **`ManuallySet`**, not to `Included` — the "remembering which" clause is the part a naive implementation drops
  - `Clear` is a no-op from `Included`, `Excluded` and `Unresolved`, including an `Excluded` that came from `ManuallySet`

  *Done 2026-09-21, verified independently.* 158 lines of production code in `Cohorts.cs`, `ICollectionStore` added verbatim to `Collection.cs` in CONTRACTS.md's own position, S0.3a's deferral NOTE deleted. Clean Release build at 0 warnings. **`Passed! Failed: 0, Passed: 28, Total: 28`** (14 inherited + 14 new).
  - **V10 is honoured properly:** one private `ProposeFromHash(candidates, good, ok)` returns the whole `(State, Chosen, ChosenDistance, IsLowConfidence)` tuple, and *both* the constructor and `Clear()` call it. The threshold comparison exists exactly once, so "what a fresh tile starts as" and "what a cleared tile reverts to" cannot drift.
  - `ToggleExcluded` remembers the pre-exclude state in a field, so `ManuallySet → Excluded → ManuallySet` round-trips instead of collapsing to `Included`.
  - `SetManually` also clears `IsLowConfidence`, which the contract does not state but follows from it: the flag is a highlight for a machine guess, and a user's own choice is not one.
  - The boundary tests use the thresholds themselves as the distances, so ≤ is pinned at both edges with all four columns asserted.
  - **The orchestrator chaos-tested the one the implementer did not:** flipping both `<=` to `<` fails 3 tests — at-ok becomes `Unresolved` instead of `Included`, and at-good reports `IsLowConfidence` true because it falls through to the ok branch. Exactly the off-by-one, reverted, 28/28 green.

> **Reorder, 2026-09-21: S0.5a and S0.5b run BEFORE S0.4a/S0.4b.** The plan lists the pipeline first, but CONTRACTS.md says the pipeline's test *is* the end-to-end suite, and that suite needs the fakes. Written in listed order, S0.4a — the highest-risk code in Stream 0, holding the lock and the frame-ownership handoff — would sit untested until S0.6a, or would need throwaway test doubles that the real fakes then duplicate. The fakes do not depend on the pipeline in either direction, so nothing is lost by swapping them, and S0.4a then gets tested against the same fakes the end-to-end suite uses. This is within the plan's own rules: S0.5a and S0.5b are marked ∥. New order: **S0.5a → S0.5b → S0.4a → S0.4b → S0.6a → S0.6b → S0.7 → S0.2 → S0.8.** S0.2 (CI) moves late because its test leg cannot pass until S0.7 adds the placeholder tests.
- [x] **S0.4a** `Core/Scanning`: `DetectionSnapshot`, `IScanPipeline` and its implementation. Rules:
  - The pipeline retains exactly one frame, guarded by a lock.
  - `CaptureAsync` takes ownership of the frame under the lock, then does its work outside the lock.
  - Detection always uses `maxCards = 9` (V12).
  - `now` comes from `startWallClock + Stopwatch.Elapsed`.
  - `NotifyCaptured()` is called on every capture, manual or auto.
  - `SourceFailed` is raised before `RunAsync` faults.
  - Thresholds come from `ScanSettings` and are passed into each tile.

  *Done 2026-09-21, verified independently.* 285 lines in `ScanPipeline.cs` + 48 in `IScanPipeline.cs`, 13 new tests, clean Release build at 0 warnings, **`Passed! Failed: 0, Passed: 93, Total: 93`**, stable across repeated runs.

  **The ownership handoff, which is the reason this package was the risky one:** one private `TryCaptureFromRetained` is the *only* code that reads or clears `_retainedFrame`/`_latestSnapshot`, and both the manual and auto paths go through it. Under the lock it either returns null without touching anything, or takes the frame **and** its snapshot together and nulls both fields — so the pipeline holds neither until the next frame arrives. Rectify and identify then run outside the lock, and a `finally` disposes the owned frame exactly once. Two callers can therefore only ever contend for the lock, never for the object.

  🔴 **The chaos test that earns the package, re-run by the orchestrator.** Removing the two lines that transfer ownership does **not** throw. It silently corrupts: the torn-frame check counted **114 bad pixel reads** on the orchestrator's run and 150 on the implementer's — the count varies with timing, as a concurrency test should. Real code: 0, and 93/93.

  That result is worth more than the passing suite. **This bug class produces no exception, no crash and no log line** — just a rectified card built from a buffer already returned to the pool, i.e. a wrong hash, a wrong match and bad inventory with nothing saying so. It is precisely what CONTRACTS.md predicted (*"fine in dev, torn frames under load"*) and why frames cross the seam as pooled `byte[]` rather than `IntPtr`. And **114 rather than 1** means the window is wide, not a lucky-timing rarity — it would have bitten on real hardware. Had the test reported 0 with the sabotage in place it would have been decoration, and the race would have shipped undetectable.

  Other verifications: `MaxDetectionCards = 9` is a named constant and detection is called with it, never with `ExpectedCount` (V12; breaking it fails the recorded-argument test with 1 and 3). Dropping the dispose-the-replaced-frame line leaks **39 of 40** pooled buffers. The only threshold reference in the file is `_settings.GoodDistance`/`OkDistance` passed straight into the `CohortTile` constructor — no comparison, no literal, which is I4 satisfied early. `SourceFailed` fires inside the `catch` before the rethrow, so the ordering is structural rather than incidental.

  Two judgement calls made by the implementer, both accepted:
  - `CaptureAsync` does **not** call `NotifyCaptured` when there were zero detections. Correct: CLAUDE.md's interaction model makes Space a no-op on 0 detections, so nothing was captured and there is nothing to re-arm from.
  - A second `_triggerLock` serialises every `IAutoCaptureTrigger` call, because the trigger is reached from both the loop thread and `CaptureAsync`'s worker and the contract promises it no thread safety. Not asked for; it is the same hazard one layer up, and stream A's A0 implementation now cannot be broken by a race it never has to think about.
- [x] **S0.4b** `ScanPipelineFactory.Create(...)`, plus the thresholds loader `ThresholdsFile.Load(path) → ThresholdsFile`. Schema v1 is JSON with these fields: `formatVersion`, `goodDistance`, `okDistance`, `referenceFloor`, `indexArtworkCount`, `indexSha256`, `measuredAt`, `notes`. Unknown `formatVersion`, a missing file or a missing field throws `InvalidDataException` or `FileNotFoundException`. Also `DataFiles.IndexPath` and `DataFiles.ThresholdsPath`, relative to `AppContext.BaseDirectory`. Accept: loader unit tests for a valid file, a missing file, a bad version and a missing field.

  *Done 2026-09-21, verified independently.* 209 lines of production code across three files, 27 new tests, clean Release build at 0 warnings, **`Passed! Failed: 0, Passed: 120, Total: 120`**.

  **The subtle part, handled correctly:** every DTO property is **nullable** (`int?`, `DateTimeOffset?`, `string?`), because `System.Text.Json` deserialises both an *absent* key and a key present with JSON `null` to a null property — so one null check per field catches both. The trap avoided is the obvious-looking alternative: a non-nullable `int GoodDistance` silently keeps its default `0` on a missing key, so "absent" becomes indistinguishable from "zero" and the loader reports success. `notes` is checked for presence but accepts `""`, since it is the one legitimately empty field and an absent key is a different thing from an empty value.

  **The chaos test produced the most useful sentence in this package's report.** Defaulting a missing field instead of throwing failed 5 of the 8 cases — and the implementer explained precisely why the other 3 passed *incidentally*: the two string fields were untouched by that particular sabotage, and a defaulted `formatVersion` of `0` still trips the separate version gate. That is the right way to report a partial result. The stated real-world consequence is also exactly right: **a defaulted `GoodDistance` of 0 would make every genuine match read as "worse than good"**, since Hamming distance is always > 0 — silently downgrading every confident hit to low-confidence, with no error anywhere. A wrong-but-plausible confidence gate is worse than a crash.

  Residual coverage gap, noted not fixed: a bug that defaulted `indexSha256` or `notes` to `""` rather than null would still pass. Both are metadata rather than thresholds, so nothing behavioural rides on them — but if either ever gains a consumer, that case needs a test.
- [x] **S0.5a** ∥ Fakes in `src/LoreFetch.Core/Fakes/`: `FolderFrameSource` plus `FolderFrameSourceFactory` (V20), `StubCardDetector` (1/3/9 layouts) and `StubRectifier`. Accept: unit tests. `FolderFrameSource` disposes every frame it drops.

  *Done 2026-09-21, verified independently.* 452 lines across four files, clean Release build at 0 warnings, **`Passed! Failed: 0, Passed: 52, Total: 52`** (28 inherited + 24 new).
  - **The drop path disposes through `Channel.CreateBounded<CameraFrame>(options, itemDropped)`** — capacity 1, `DropOldest`, callback disposing the evicted frame. That overload is the *only* correct answer here and the contract says so: a plain bounded channel does not dispose what it evicts. `DisposeAsync` additionally drains any frame still sitting in the channel, covering the case where `ReadAsync` was never called at all.
  - `ArrayPool<byte>.Shared` throughout; `grep` for `ArrayPool.Create` over `src/` is empty.
  - `StubRectifier` is genuinely managed-only — no `using OpenCvSharp`, no `Cv2.`, no `Mat`. `FolderFrameSource` is the single fake that uses OpenCvSharp, for `ImRead`, which is correct. `Core/Abstractions` remains CV-free.
  - **The orchestrator re-ran the leak chaos independently:** dropping the `itemDropped` argument fails 2 tests, leaking **35 and 11 pooled buffers** respectively. That is real leak detection on rent/return counts, not an incidental assertion. Reverted, 52/52 green.
  - Rectifier clamp chaos: unclamped bounding boxes **throw** `ArgumentOutOfRangeException` at the `Span.Slice` rather than silently misreading — worth knowing, because a throw is the recoverable failure and a silent misread would have been a corrupt thumbnail nobody notices.
  - 452 lines, against what the brief template called a ~400-line **stop** threshold. The implementer flagged it and finished rather than cutting scope to fit — the right call, and four real classes justify it: `FolderFrameSource` is the shipping demo path rather than a test double, plus row-by-row `Marshal.Copy` for stride safety and house-style doc comments.

    **The threshold itself was miscast, and §0's template is corrected as a result** (user's call, 2026-09-21): it is a **scope-drift tripwire, not a line budget**, and it now says *mention it and keep going* rather than *stop*. Phrased as a STOP condition it invited the one response nobody wants — an implementer deleting a guard, a doc comment or a test to get under a number, or abandoning a package mid-way over its size. The question it should ask is "is this package doing more than one package's job?", which 452 lines of four cohesive fakes answers no.
- [x] **S0.5b** ∥ `StubCardIdentifier` (configurable distances), `StubOracleCatalog` and the two collection stubs:
  - `StubOracleCatalog` defaults to 33,000 synthetic entries. It always includes the hostile names: `Kongming, "Sleeping Dragon"`, `"Rumors of My Death . . ."`, `Lim-Dûl's Vault`, `Borrowing 100,000 Arrows` and `+2 Mace`.
  - `StubCollectionStore` has real dedup semantics and a switch to throw on the next commit.
  - `StubCollectionExporter` takes a configurable `ExportFormat`.

  Accept: unit tests.

  *Done 2026-09-21, verified independently.* 302 lines across four files, clean Release build at 0 warnings, **`Passed! Failed: 0, Passed: 80, Total: 80`** (52 inherited + 28 new).
  - The orchestrator checked the five hostile names **at runtime, by exact string equality, rather than by reading the source** — all five present verbatim including `Lim-Dûl`'s non-ASCII `û`, and `All.Count` is exactly 33,000. If the count is configured below five the hostile handful still lands, which is the right precedence.
  - The null-vs-`""` Condition chaos was **re-run by the orchestrator**: injecting `(r.Condition ?? "") == (key.Condition ?? "")` fails exactly one test, the new one, `Expected: 2, Actual: 1`. Reverted, 80/80 green.

  🔎 **The finding that matters here is for stream D, not for the stub.** Asked to chaos-test the null-vs-`""` dedup bug, the implementer reported plainly that **the existing 79 tests did not catch it at all**, and added one — rather than reporting a pass. The reason is structural and it applies to the real store just as much: **`CommitCohortAsync` can never produce a non-null `Condition`**, because `CohortTile` carries none and v1 never assesses condition. So the `Condition` half of the `OracleId` + `Condition` dedup key is **unreachable through the commit path**, and a bug that conflates `null` with `""` is invisible there — while being exactly the bug CONTRACTS.md warns "would silently split one card into two rows".

  Consequences to carry into **D1 and D3**: the condition half of dedup is only reachable through the **reader**, on a file that already contains conditions. So D's tests for it must go through a written file, not through a commit, and must include the `null` vs `""` pair explicitly. A D3 suite that only exercises `CommitCohortAsync` will pass with that bug present.
  - `StubCollectionStore` gains a `Seed(CollectionRow)` method outside `ICollectionStore`, which is what made the case reachable at all. Accepted, and it is more than a test seam: stream A needs a populated collection to render its `DataGrid`, its sort and its empty-state boundary, and seeding beats committing synthetic cohorts to get there.
- [x] **S0.6a** `Tests/Integration` harness:
  - an `IImplementationSet` provider with `Fakes` and a skipping `Real` stub (V8)
  - the `LOREFETCH_REQUIRE_REAL` switch (V7)
  - a frame generator writing to a temp folder (plain fills with drawn rectangles)
  - `ScriptedTrigger` (V9)

  Cases: an X'd tile is absent; Escape writes nothing; `ManuallySet` commits as `Source=Manual` with no distance; a cleared tile commits the machine's proposal; re-commit increments the quantity; a 7-of-9 partial cohort commits 7.
- [x] **S0.6b** More `Tests/Integration` cases:
  - `CaptureAsync` rectifies the latest snapshot's frame, and is hammered concurrently with the loop
  - every pooled frame is disposed exactly once over a sustained run, using a counting pool
  - a thumbnail is still readable after commit
  - `SourceFailed` fires on a throwing source
  - `AutoCaptured` fires via `ScriptedTrigger`

  Accept for S0.6a and S0.6b: `dotnet test Tests/Integration` is green, and every `Real` skip gives its reason.

  *Both done 2026-09-21, verified independently in **both directions**, which is the only verification that means anything for this pair.* With the `ArtworkId` coverage that followed: **`Passed! Failed: 0, Passed: 143, Skipped: 8, Total: 151`**, and `LOREFETCH_REQUIRE_REAL=1` → **`Failed! Failed: 8, Passed: 143, Skipped: 0`**, exit 1. Exactly the 8 `Real` cases flip and nothing else, and each skip carries its reason through the single `RealArtifactGate.SkipOrFail` helper. **V7 is a mechanism now, not a claim** — I6's acceptance on the PC is this command.
  - **V8 resolved properly:** `RealImplementationSet` compiles today without naming one type that does not exist yet — `HashCardIdentifier`, `CsvCollectionStore` and the rest appear only in comments. Code that cannot compile cannot be skipped, so the indirection is load-bearing.
  - S0.6a's second chaos case is the one worth keeping: stripping the env-var check made the run report `Passed! … Skipped: 7` **with the variable set** — the guard silently absent. Proving a guard's *absence* is detectable matters as much as proving its presence.
  - **S0.6b declined to write three of its five nominal cases, and was right to.** (a) concurrency, (d) `SourceFailed` ordering and (e) `AutoCaptured` were already covered at equal or greater fidelity by S0.4a and S0.6a; it named the covering test for each. A near-copy doubles maintenance and creates false confidence that two independent things are guarded.
  - Its gap-finding was sharp: S0.4a's dispose-accounting test **never calls `CaptureAsync`**, so it exercised only `ProcessFrame`'s swap-and-dispose site and never `TryCaptureFromRetained`'s separate one. The new case runs 500 frames with 20 interleaved captures so **both dispose sites are live at once**. Chaos: a pool wrapper swallowing one `Return` fails it at 500 vs 499.
  - The genuinely new end-to-end case is **thumbnail-survives-commit**, the regression guard for the `RectifiedCard` disposal footgun CONTRACTS.md says was "removed by construction". It asserts real pixel bytes with a non-uniformity guard against a vacuous all-zero comparison; chaos (a decorator zeroing each committed tile's buffer) fails it on the exact pixel signature.

  🔎 **Finding for stream D, which corrected the orchestrator's own prediction.** Testing the `ArtworkId` rules, I predicted that breaking `CohortTile.SetManually` would fail both the tile-level and the store-level Manual case. **It does not fail the store-level one**, and the reason is structural: `StubCollectionStore` derives the row's `ArtworkId` — and, pre-existing since S0.5b, its `BestMatchDistance` — with its own independent `tile.State == ManuallySet ? null : …` guard. So the store never trusts a manual tile's values, and a tile-level regression is **invisible at the store layer**.

  That is defensible as defence-in-depth, and it is not a contract violation — the store still produces what `ICollectionStore` promises. But it duplicates a rule, which is the same class of thing V10 was created to prevent, and it has a consequence **D3 must decide deliberately rather than inherit**: if the real `CsvCollectionStore` *trusts* the tile instead of re-deriving, a `CohortTile` regression leaks a stale art id and a stale distance into the user's file — and the stub's behaviour will not have warned anyone. Whichever D3 picks, its Manual-row tests must construct a tile whose `ChosenArtworkId` is set (seeded, not via `SetManually`) or they cannot tell the two designs apart.

  *Done 2026-09-21 by the orchestrator rather than dispatched: the design hinges entirely on the `dotnet test` measurement above, so the brief would have been longer than the file.* Two legs with `fail-fast: false`, so one leg's failure cannot hide the other's — they test different propositions, not the same one twice — plus `concurrency` with `cancel-in-progress`, and .NET from `global-json-file: global.json`. The YAML was parsed and **asserted programmatically**: it must invoke the script and must not contain `dotnet test`. The exact CI command was then run locally from the repo root, green.

  `Restore` and `Build` stay as separate steps even though `lorefetch.sh test` builds too, so a build failure reads as a build failure in the CI UI instead of being buried in a test step. The duplicate work is seconds of incremental build.

  The file itself documents what is deliberately *not* run: Accuracy (a committed measurement, and it needs the uncommittable fixture corpus), Hardware (needs the C920), and the `Real` cases, which skip by construction because they need the index and the Scryfall cache — which is exactly why "integration is done" is V7's local `LOREFETCH_REQUIRE_REAL=1` run and not zero skips in CI.
- [x] **S0.7** Scaffolding:
  - one placeholder test per `Tests/StreamX` project, so each builds and runs
  - README: add a `## Stream A — UI` … `## Stream D — Collection & export` section skeleton, and keep the existing content
  - `THIRD-PARTY-NOTICES` untouched (entries are added at integration)
- [x] **S0.9** 🧭 `scripts/lorefetch.sh` and the `lorefetch-run` skill — **one entry point to pull, build, test and run, on either machine.** Written 2026-09-21 at the user's request, deliberately **out of order** (before S0.1) because the platform switch means the PC now consumes what the Mac pushes and needs a single command to do it. It is therefore **shape-agnostic**: it discovers the solution and the app project instead of hardcoding them, and when neither exists it says so and exits 0 rather than emitting a confusing MSBuild error. Commands `doctor | pull | build | test | run | all`, plus `--no-pull --debug --verbose --all-tests --hardware --filter <expr> -- <app args>`.

  POSIX `sh`, matching the prior art in `hooks/` and `.claude/hooks/` — it runs under Git Bash on the PC, which those already require. The skill is a thin pointer to the script and is **the one committed skill**, so `.gitignore` negates it out of the `.claude/skills/*` ignore.

  Verified against a throwaway `.slnx` solution in the scratchpad (an exe project plus an xunit v3 test project with one plain, one `WindowsOnly` and one `Hardware` test): build, `.slnx` handling, per-platform filtering, app arg passthrough, the dirty-tree pull refusal, and every argument-error path. **Two findings came out of that and are carried as overrides below — see S0.1 and S0.2.**

  *Re-verify after S0.1 lands, when there is a real solution to point it at.*
- [x] **S0.8** 🧭 **Freeze review.**
  - Diff `Core/Abstractions` against CONTRACTS.md member by member.
  - Confirm the identity guard with a refused commit (`GIT_AUTHOR_EMAIL=someone@example.com`).
  - Run `dotnet test` on the whole solution.
  - Commit `Stream 0: foundation`.
- [x] **P1** 👤 Push checkpoint. Show the summary and wait for approval, then push `main`. Both CI legs must be green, and a red leg sends the work back to S0.

  *Done 2026-09-21. **Both legs green on the first run**, CI run `35658194631` at `96b04b1`:*

  | leg | result |
  |---|---|
  | `windows-latest` (ship target) | success |
  | `macos-latest` (portability guard) | success |

  **The Windows leg is the one that mattered**, because it is the first thing in this project verified on the ship target rather than on the Mac. It proves three claims that were until now only argued: `net10.0` plus the entire pinned package set restores and builds on `win-x64`; `scripts/lorefetch.sh` runs under Git Bash on `windows-latest`, which is what lets one script own the test filter for both legs; and `Core` carries nothing that breaks on either platform. The macOS leg proves the converse — no Windows-only dependency crept into `Core` — which is its entire job.

  ⛩ **G1 is open.** Everything under §0's *Worktrees* may now proceed: four worktrees, and from that moment `Core/Abstractions`, `Core/Scanning`, `Core/Fakes`, `Tests/Integration`, every `.csproj`, the `.slnx`, the `Directory.*` files and `global.json` are frozen — mechanically, from any cwd, for any target path inside a linked worktree (G0.6).

### Machine split — ruled 2026-09-22 (user)

**At the start of each session the user says which machine it is on, and whether they have overridden this split for that session.** Do not infer the machine from paths or from this table; ask if the user has not said. **An override from the user beats this table** for that session. If the user says which machine but gives no override, this table decides the work.

| Machine | Owns | Why |
|---|---|---|
| **Windows PC** | **All of stream B** (B7, B2, B5c, B6, B8) · **C2** (FlashCap shim, tested against the real camera) · **C4** · **A10** · **I6** · **E1, E2** | B needs the 5.1 GB image cache, which lives only here. The committed index must be built on win-x64. Every committed threshold (B2's `referenceFloor`, B6's `goodDistance`/`okDistance`) must be measured on win-x64, because `INTER_AREA` is not bit-exact on ARM64 and that includes the query side's 32×32 resize. The C920 is attached here, and A10, I6 and the E items are win-x64 runs by nature (see *Platform switch*). |
| **Mac** | **A4–A9** · **D4, D6** · **C3** | All are built and tested against fakes or plain files, with no camera, image cache or measured threshold involved. |
| **Either** (the user's hands) | D5 (the real Moxfield import), H items | These need the user, not a particular machine. |

Rules that follow from the split:
1. **Tick only your own machine's items, and pull `main` before committing a tick.** Both machines tick in this file. The checklist is split by stream, so pull-then-commit keeps rebases trivial. Never resolve a conflict in this file by dropping the other machine's lines.
2. **Work moves between machines only through GitHub**, so every hand-off needs a push, and every push needs the user's approval (§3). Push a stream branch when its machine's session ends.
3. **Code tested on the Mac meets Windows first in CI's Windows leg** at merge time. A10 and C4 on the PC remain the real acceptance for A and C.
4. **If the Mac ever has to run a B package** (by override), it may use the committed index as is, but it **must not rebuild the index, regenerate goldens, or commit a measured threshold**. It needs the cache copied outside git (not re-pulled with a fresh `bulk`: a new day's bulk file drifts from the committed index), and a copy of `scryfall-bulk/filtered-artworks.jsonl` to drive `images --manifest`. Then `test-images/ad-hoc/` needs copying by hand as well.

### Handoff — end of the Mac A/D session, 2026-09-22

**Newest handoff; the ones below still apply.** Covers the Mac's lanes only (A, C3, D). The B/PC handoffs below remain the truth for their lanes. Checkboxes and their per-item notes are authoritative.

**State — the Mac's entire workload is complete.** A4, A5, A6, A7 (+ the retain-on-lock test), A8, A9 are ticked; C3 is ticked; D4, D5 (real Moxfield import verified) and D6 are ticked. `stream/a` is rebased onto `origin/stream/a` (`9e4c228`) with A4–A9 replayed on top (tip `ac55852` pre-rebase), **ahead 8 / behind 0 — a clean fast-forward**, clean worktree. StreamA is **106 passed / 0 failed** (2 `WindowsOnly` screenshot tests excluded on macOS), Integration **143 passed / 8 skipped**. Each A package was verified independently (scope, build, both suites, smoke-launch, `grep -ri moxfield src/LoreFetch.App` empty) plus one un-briefed chaos case — which found and closed a real gap in A7 (retain-cohort-on-`CollectionStoreException` was untested; `97615f3` covers it).

**Push status.** `stream/c` (`7bf2632`) and `stream/d` (`fb24715`) were pushed earlier this session; the PC has since **merged `stream/c` to `main`** (Stream C complete — C2 + C4 hardware-verified on the C920). `stream/a` and the A5–A9 `main` ticks are pushed at session end **with the user's approval**.

**Next for the PC (A lane):** **A10** 🧭👤 — the done-when hardware run on `stream/a`: all three layouts, a keyboard-only loop, all four tile states reached (two via configured `StubCardIdentifier` distances, two via user action), memory flat for 5 min under `dotnet-counters`; record fps + memory, then write **README §A**. **A10 is A's merge gate → P2.** Everything A needs is on `stream/a`; it depends on nothing from B or D. C4 is already done. B continues per its own handoff (B7 → B2 → B5c → B6 → B8).

**Adjacent finds.**
1. 9 pre-existing `xUnit1051` analyzer warnings in `Tests/StreamA/TileInteractionTests.cs` (A6) — non-blocking; the repo already treats `xUnit1051` as worth fixing (see C1a). A cheap cleanup when A is next touched.
2. The named-chaos escape-valve earned its place again: A7's brief-named chaos all failed correctly, but the *un-briefed* case is what surfaced the retain-on-lock coverage gap. Same lesson as the B session's finding #4.

### Handoff — end of the B4–B5b session, 2026-09-22, Windows PC

**Read this first; the older handoffs below still apply.** The checkboxes and their notes are the truth. This section adds only what is not recorded against an item.

**State.** B4a, B4b, B4c, B4d, B5a and B5b are ticked. `stream/b` is at `9679148`, **not pushed**, with a clean worktree. StreamB has 163 tests; Integration is 143 passed / 8 skipped / 151. `main` holds the ticks. **The full index is committed on `stream/b`** (`data/index/cards.lfidx`, SHA `6495314e…`), so it reaches `main` only when B merges. Streams A, C and D are untouched since the first session.

**Next for B, in order:** **B7** (synthetic generator) → **B2** (round-trip gate) → **B5c** (crop-scale curve) → B6, which is gated on H3 → B8. B2 is **unblocked now**: the cache and the committed index both exist on this PC. B7 before B2 is only a suggestion; they are independent. B5c now has concrete evidence to start from (B5b's note). It should measure the floor against the inset *and* decide whether the fix belongs in the detector (finding the outer edge) or in the identifier (a scale sweep).

**Where things live on this PC. Everything below is outside git by design.**

| Path | Contents |
|---|---|
| `C:\LoreFetchData\scryfall-cache\` | 48,750 `normal` renders, `<ArtworkId>.jpg`, 5.1 GB |
| `C:\LoreFetchData\index-out\` | `cards.lfidx` (the committed one), `subset-5000.lfidx`, `.build.json` sidecars |
| `C:\LoreFetchData\detect-out\` | B5a/B5b overlays and rectified crops |
| `.claude\worktrees\stream-b\scryfall-bulk\` | bulk `.jsonl.gz` files and `filtered-artworks.jsonl`, the manifest (gitignored) |
| `.claude\worktrees\stream-b\test-images\ad-hoc\` | **a copy** of main's 12 ad-hoc frames (gitignored). Tests resolve `test-images/` from the checkout they run in, so every worktree needs its own copy. |

**What this session learned**

1. **The image pull is minutes, not hours.** 48,750 images took 10.7 min (76/s, 0 failures), and building the index takes 40 s. Rebuilding the index is cheap, which makes "rebuild on any transform change" a practical rule rather than a threat.
2. **Lab finds the repo by walking up from its own exe or the cwd.** A build copied outside the repo cannot run `bulk` or `images` without explicit paths. B4c fixed the crash. Run Lab from the worktree's `bin/`, and **do not rebuild the Lab while a long Lab run is using it**: Windows locks the dll, and the implementer's build fails.
3. **"Exactly one detection" is a weak real-capture assertion.** It passed on Plains on black while the quad sat on the wrong edge, and the result was a confident wrong identification. Only looking at the overlay and crop caught it. For real-capture checks, look at the pictures. B5b's `identify` table is the better check.
4. **Chaos cases the brief named left tests green 3 times in B5a** (min-area, ordering, dedupe), and the implementer strengthened each. The escape-valve clause is still earning its place.
5. **B3b's 50 ms timing test soft-skips** when the full suite runs alongside CPU-heavy tests. Read that number only from an isolated run.
6. **`core.autocrlf` is on and there is no `.gitattributes`.** `cards.lfidx` survived byte-identical only because git's NUL-byte heuristic flags it as binary. Add `*.lfidx binary` on `main` (a `main`-only file, not frozen) before the index merges.
7. **The user asked whether a white mat without sleeves is best.** The answer given: it is the safest default for detection on this evidence (4/4 detected and correct), but it is not settled. Sol Ring on black gave the best distance of all (94). Black fails only when a card's border merges into the mat. With one sleeved card, sleeve and card cannot be separated. B6 on H3 decides.

### Handoff — end of the B3b session, 2026-09-22, Windows PC

**The two older handoffs below still apply.** State: B3b ticked. `stream/b` is at `1b4f8d3`, **not pushed**, with a clean worktree. `main` holds this tick. Every other stream is untouched since the first session. **Next: B4a**, then B4b → B4c, and start B4d's full image pull as early as possible so it runs unattended.

B4a brief notes, already worked out, so the brief can be written straight from them:
- **Lab has only a stub `Program.cs`.** B4a adds the command dispatch (hand-rolled argument parsing; no new package, since `.csproj`s are frozen) and uses `System.Text.Json` for the JSONL.
- **The cascade applied is stream-b §B4's table through the set-type step:** 48,713 arts over 33,578 oracle ids, which matches CLAUDE.md's index count. The `frame == "2015"` row (31,684) is **printed as information only, not applied**: CLAUDE.md's index figure excludes it, and older-frame cards identify just as well. Print the count after every step, and skip entries without top-level `image_uris` explicitly, with that count shown too.
- **Output:** a filtered manifest (id, oracle_id, name, type_line, `image_uris.normal`, `IsBasicLand` = `type_line` starts with `Basic Land`) for B4b and B4c to consume. The downloads go in gitignored `scryfall-bulk/`. The cache path is a parameter.
- **The unit test's JSONL sample is synthetic:** records shaped like Scryfall's schema, with made-up names and no real oracle text. It covers one record per cascade branch, including a multi-faced object with no top-level `image_uris`.
- **Open question (single-printing fraction):** a separate `printings` subcommand over `default_cards`. It keys in-scope printings (English, has a `nonfoil` finish, single-faced, 2015 frame) by `illustration_id` and reports the fraction of in-scope arts with exactly one. Report the exact filter used alongside the number.
- The standard brief additions apply: quote the test summary lines verbatim; name the chaos cases (e.g. drop the `image_uris` skip, which should crash or miscount; invert the `lang` filter); and rebuild rather than `--no-build` when chaos-testing.

### Per-project READMEs — ruled 2026-09-22

Each of the nine projects has a `README.md` on `main` (`b0a295b`) that holds **orientation only**: purpose, owner, dependency rules, freeze, how to run its tests, and links into `docs/`. **Streams do not edit these files mid-stream.** `Core`'s README is shared by A, B and D, so concurrent edits would be exactly the multi-way conflict the freeze exists to prevent. Each stream fills in the "Internals" line of its own projects' READMEs **at its done-when step** (A10, B8, C4, D6), next to the root README section it already owes. The same step flips its "(not yet on `main`)" markers in `Core`'s folder map.

### Handoff — end of the first stream session, 2026-09-21 ~23:55, Windows PC

**Read this first; the S0.8 handoff below is older and still applies.** The checkboxes are the truth. This section holds only what the session learned that is not already recorded against an item.

#### State

- **13 packages ticked:** A0–A3, B1a, B1b, B3a, C1a–C1c, D1–D3. Each one was independently verified (§0 steps plus one orchestrator chaos case) before its tick.
- **Everything is pushed:** `main` at `50c1214` with CI green on both legs, and `stream/a`–`stream/d` on GitHub. No stream is merged: none has finished its list, which the merge gate requires. **Stream code has only been proven on this PC**; CI runs on `main` alone, so the macOS leg first sees stream code at each merge.
- **Worktrees on this PC** (`.claude/worktrees/stream-<x>`) are clean, each at its pushed branch tip. The stale `agent-arch-review-plan-f0e4ed` worktree holds pre-rewrite history and can be removed.
- **Next per stream:** A4 · **B3b** · C2 · D4. Remaining: 25 stream packages, 9 integration/endgame, 4 human items.

#### Next session: stream B, solo, on this PC

Stream B is the critical path: about 12 serial packages, plus B4d's multi-hour image pull. This PC is the right machine, because B1b's goldens, B4d's index and every golden run are win-x64 by nature.

- **Order:** B3b (the identifier, over the B3a index), then **B4a → B4b → B4c, and start B4d's full run as early as possible** so the ~6 GB pull runs unattended while B5a/B5b/B7 proceed. The cache lives outside the repo at `C:\LoreFetchData\scryfall-cache`, passed as a parameter. If B4d looks too slow, the plan allows a labelled 5k-art subset.
- **B4a carries an open question:** the single-printing fraction. It needs `default_cards` as well as `unique_artwork`.
- **B6 is gated on H3,** the user's fixture corpus, which needs H1 (the printed mount). If H1 and H3 are late, B stops at B2/B5c.
- **Working in the worktree:** `stream-b` is a linked worktree, so `guard-write.sh` freezes the contract surface there. A session working *from the main checkout* is **not** frozen when it writes main's own files, so keep every write inside `.claude/worktrees/stream-b/`. Build and test there with `.claude/worktrees/stream-b/scripts/lorefetch.sh test --no-pull`.
- **Current B baseline:** 45 StreamB tests, 8 of them `WindowsOnly` goldens (37 run under the macOS filter). Integration 143 passed / 8 skipped / 151 total.

#### What this session learned

1. **Verification caught what implementer reports missed, every time it looked.** Without the orchestrator's own chaos case, these would all have been ticked:
   - the trigger fired on a slowly moving hand (A0);
   - a changed resize filter was invisible to B1a's 29 tests;
   - a failed store commit's temp-file cleanup was untested, because `FileShare.None` fails before the write path (D3);
   - the watchdog stall test passed at the wrong timeout (C1c);
   - a preview data race tore frames (A2).
   Keep breaking one invariant per package yourself. **The best chaos case is the one the brief didn't name.**
2. **Smoke-launch anything with a UI thread.** A3's crash (`RequestAnimationFrame` called off the UI thread) and A1's temp-folder leak (Avalonia's `Shutdown()` never raises `ShutdownRequested`) were invisible to every unit test. Launch with `LOREFETCH_SMOKE_EXIT_MS=3000`, check exit 0 and the `%TEMP%` folder count, and never leave a window open.
3. **A usage limit hit mid-chaos-test leaves the mutation in the code.** It happened twice: UTF-16 strings in B3a, and D3's cleanup call commented out. After any cutoff, grep every worktree for `chaos` and check `git diff` before resuming or committing.
4. **Stop at package boundaries, not mid-edit.** Near a limit, say "finish and commit your current package, then stop" rather than killing agents. And have implementers commit as each step passes.
5. **A fresh agent with a tight brief is usually cheaper than resuming.** Resumed contexts ran 200–300k tokens; a fresh brief pointing at the files and the specific defect was cheaper and did as well. Resume only when the agent holds mid-edit reasoning the repo doesn't.
6. **Four parallel streams hit the usage limit** within about 5 hours, including review. Two at a time, or B plus one, is the sustainable width.
7. **The orchestrator must not implement.** It drifted into writing a test once and was rightly stopped. Its own writes are the plan, the ticks, and reverting a known chaos mutation.
8. **Stale wording to fix on `main` when convenient:** TESTING.md §Unit says movement beyond ε re-arms the trigger. The ruling in stream-a A0 says only a count change does (see A0's tick).

### Handoff — written at S0.8, 2026-09-21

**Stream 0 is complete. G1 is the next gate, and P1 (the push + both CI legs green) is the only thing between here and forking.** This section exists because the orchestrating session that built Stream 0 ended here deliberately, and four things it learned live nowhere else. Everything else is already recorded against its own item — that was the point of writing findings down as they landed rather than saving them for a summary.

**Read this file first. The checkboxes are the truth**, not any summary, including this one.

#### 1. What every brief carried beyond §0's template

The template is necessary and not sufficient. These additions are why packages came back with real findings instead of clean-looking passes, and a brief without them will get worse work:

- **"Do NOT trust exit code 0 from `dotnet test`."** State the measurement: it exits 0 while running **zero** tests, both when no adapter is registered and when a filter matches nothing. Require the implementer to read the `Passed:`/`Total:` counts and **quote the summary line verbatim** in its report. Without this, several packages would have reported success having run nothing.
- **Per-package chaos cases, named explicitly, with an escape valve.** Do not just cite the standing practice — say *which* invariant to break and what failure to expect. And always add: *"if nothing fails, say so plainly — that means the test is too weak, and it needs strengthening rather than a passing grade."* That clause produced the single most valuable result in Stream 0 (S0.5b's null-vs-`""` coverage hole, which 79 tests missed) and it is what stops an implementer reporting a vacuous pass.
- **"Mention but do NOT stop or cut scope for ~400 lines."** The template originally had this as a STOP condition, which invites deleting a guard or a test to get under a number. It is a scope-drift tripwire.
- **Read-the-existing-code-first, with counts.** Tell the implementer how many tests currently pass and that every one must stay green. Then, where a package's nominal case list overlaps earlier work, **require it to justify what it adds and name the test that already covers what it skips.** S0.6b correctly declined three of five cases on that instruction — and found a real gap in the fourth.
- **Name the write scope as globs and say "anything else is out of scope."** Every package respected it; the one deviation worth having (`StubCollectionStore.Seed`) was reported rather than smuggled.

#### 2. Verify packages; do not accept reports

§0's five verification steps are the floor. What actually caught things: **independently re-running one chaos case per package** rather than trusting the implementer's account of it. Twice that changed the outcome — a boundary off-by-one in S0.3b that the implementer had not tested (flipping `<=` to `<` fails 3 tests), and confirmation that breaking the pipeline's ownership transfer yields **114 silent torn-frame reads** rather than a throw. Once it corrected *me*: my prediction that a `SetManually` regression would fail the store-level test was wrong, for a structural reason now recorded against S0.6b.

Also: after any commit that was supposed to tick a checkbox, **re-read the checkbox.** A plan-edit script silently no-op'd twice while the commit went ahead anyway.

#### 3. A fresh clone has NO guards. Run `scripts/lorefetch.sh setup`.

`hooks/pre-commit` is tracked so the *file* survives a clone, but **`core.hooksPath` is config and config does not clone** — and with `user.email` unset, git falls back to the global identity, which on these machines is a work address. So a fresh clone commits under the wrong identity into a public repo **with the hook that exists to refuse that not running at all.** Verified on a real clone. `scripts/lorefetch.sh setup` wires identity, `hooksPath` and the SSH key pin, repo-locally; `doctor` now **exits non-zero** when the guards are not live rather than mentioning it among other notes.

#### 4. History was rewritten on 2026-09-21. Every SHA changed.

Any clone predating that must `git fetch && git reset --hard origin/main` — **not** merge, which would resurrect the old history. Prefer the in-place reset over re-cloning precisely because of §3. A bundle of the pre-rewrite state was left in that session's scratchpad and is gone now; the rewrite was verified by cloning from GitHub and grepping every commit's content, messages and trees.

#### 5. Open threads deliberately left

| Thread | Where |
|---|---|
| The single-printing fraction, deferred from the `ArtworkId` ruling | open question on **B4a** |
| Whether the real `CsvCollectionStore` re-derives or trusts the tile for `Manual` rows | finding under **S0.6b**, decided by **D3** |
| `null` vs `""` and the `ArtworkId` fold must be tested through a written **file**, not a commit | overrides on **D1/D3** |
| `indexSha256`/`notes` defaulting to `""` is not covered | noted under **S0.4b** |
| `InternalsVisibleTo` Capture → Tests.StreamC is declared but unprovable — `Capture` has no code yet | **C1a** should replace StreamC's placeholder with a real internal-touching test |
| `THIRD-PARTY-NOTICES` advisory is non-empty by design until **I5** | the hook prints it, deduplicated, on every commit |

### Approved frozen-surface change — `AvaloniaUI.DiagnosticsSupport` Debug-only, 2026-09-22 (user)

`src/LoreFetch.App/LoreFetch.App.csproj` is frozen; the user approved this change explicitly, which is what the freeze process requires. Landed on `main` as `fb78c66`.

**Why:** the package (2.2.3) declares **no licence at all** — no `<license>` element in its `.nuspec`, no licence file in the package. No licence means no grant to redistribute, which is worse than a restrictive licence because the terms are simply absent. It was referenced unconditionally, so it would have shipped inside the `win-x64` single-file exe, contradicting RECONCILIATION's "nothing is redistributed".

**Why Debug-only rather than removal:** there are **no `AttachDevTools()` call sites anywhere in `src/`**, so the package is currently unused and could have been deleted outright. It is kept under `Condition="'$(Configuration)' == 'Debug'"` because A10 is a hands-on UI run where DevTools is exactly the tool you would want, and the condition costs nothing.

**Verified rather than assumed**, because conditioning a `PackageReference` on `$(Configuration)` is not automatically reliable — NuGet restore is configuration-agnostic. A plain `win-x64` publish (used instead of `PublishSingleFile`, which bundles the file list out of sight) contains no `AvaloniaUI.DiagnosticsSupport.Avalonia.dll` and no `Avalonia.Diagnostics*`; `bin/Release/net10.0/win-x64/` and the App's whole `obj/` tree agree. `project.assets.json` still lists the package — inert, never copied in Release. The simple condition sufficed; no `ExcludeAssets` workaround needed. Debug output still carries the assembly. **Also proved in passing that the documented ship command works cross-platform from the Mac**, producing a 201 MB exe, inside CLAUDE.md's predicted 150–250 MB.

⚠ **Owed: merge `main` into every stream branch.** The freeze process requires an approved change to land on `main` and then be merged into each stream. Deferred deliberately while an implementer was live in `stream-b`; do it at the next package boundary. Only `stream/a` (A10 pending) and `stream/b` are still active lanes.

### Empirical finding — the win-x64 goldens pass on ARM64, 2026-09-22

**Measured, not inferred: all 8 of B1b's `WindowsOnly`-traited golden hashes, generated on win-x64, pass bit-exact on this arm64-darwin Mac** (`--filter Category=WindowsOnly` → 8 passed, 0 failed).

That **contradicts the premise behind the trait**, which CLAUDE.md states as "since `macos-latest` is ARM64, a shared golden cannot pass there, so the leg would be permanently red". On this machine it passes.

**Likely mechanism, worth stating because it is reassuring rather than lucky:** the hash is a *thresholded* bit pattern — each bit is "pixel > the median of its own 8×8 cell". A sub-LSB interpolation difference only flips a bit if that pixel straddles its cell median. So the 1024-bit quantisation absorbs precisely the class of error `INTER_AREA`'s ARM64 divergence produces. 8 goldens × 1024 bits = 8,192 bit observations in agreement.

**Evidence, not proof** — a pixel sitting exactly on its cell median could still flip, and OpenCV #24163 is a real confirmed bug. So `thresholds.json` stays `provisional` until win-x64 re-measurement. But two consequences are worth acting on:
1. **The `WindowsOnly` trait on the goldens looks unnecessary**, and `scripts/lorefetch.sh`'s macOS filter is discarding a guard that would in fact pass. Revisit — this is evidence against a recorded ruling, so it is the user's call, not a unilateral change.
2. **Mac-measured thresholds are far more likely promotable than assumed.** B2's witness will settle it from the other direction the moment the PC regenerates it.

### ⛩ G1 — Fork gate
Open only when G0.* and S0.1–S0.8 plus P1 are all ticked. S0.0 may be waived. Then create the four worktrees (§0). From here on, `Core/Abstractions`, `Core/Scanning`, `Core/Fakes`, `Tests/Integration`, every `.csproj`, the `.slnx`, the `Directory.*` files and `global.json` are **frozen**. A stop-and-ask from any stream is taken to the user, and a change that is approved lands on `main` and is then merged into every stream branch.

*Forked 2026-09-21 on the **Windows PC** (`C:\Repos\LoreFetch`), from `main` at `dc1530a`: `stream/a`–`stream/d` in `.claude/worktrees/stream-<x>`.* `guard-write.sh` was re-tested against the live worktrees with 10 synthetic payloads: denied `Abstractions`, `Scanning`, `Fakes`, `Tests/Integration`, a `.csproj` and `global.json` inside a worktree, plus a write outside the repo; allowed each stream's own scope and `main`'s contract files. **A harness pitfall worth knowing:** hand-escaping Windows paths into the JSON payload with `printf` yields invalid JSON, `jq` exits 5, and every case reads as *allow* — build the payload with `jq -n --arg` instead.

**Paused 2026-09-21 (usage limit), on the Windows PC — resolved the same night; every row below is now ticked.** Kept as the record of the pause. **Found on resuming:** the limit had cut off two implementers mid-chaos-test with the mutation still in the code (UTF-16 strings in B3a, the temp-file cleanup commented out in D3). A limit hit mid-chaos can leave a planted bug in a worktree, so grep every worktree for chaos markers before resuming work there. Implementers were stopped mid-package, so worktrees hold **unverified** work — committed and uncommitted. Nothing is ticked that was not independently verified. To resume, verify each stream's commits since the last ticked one, have the uncommitted files finished (or discarded) by a fresh implementer, then continue:

| Stream | Ticked | Committed, not yet verified | Uncommitted in the worktree | Next |
|---|---|---|---|---|
| A | A0, A1 | `d5b0b2d` temp-folder cleanup (the A1 follow-up) | — | A2 preview, A3 overlay (briefed, not started) |
| B | B1a | `3d28cea` B1b goldens | B3a index format, partial: `src/LoreFetch.Core/Identification/`, `Tests/StreamB/HashIndexFileTests.cs` — **was mid-edit with a known missing brace** | verify B1b (chaos: `INTER_LINEAR` in step 5 must fail a golden), then finish B3a |
| C | — | `f4a459f` C1a + `84979d7` fixups (0 warnings, bounded enumerator test), `1f8380b` C1b | C1c watchdog, partial: `FrameWatchdog.cs`, `FrameWatchdogTests.cs` | verify C1a/C1b, finish C1c |
| D | D1, D2 | `690ecce` D3 | the D3 test-gap fix, partial: `CsvCollectionStoreWriteSequenceTests.cs` (a failed commit must leave no temp file; removing `TryDeleteBestEffort` currently leaves all 57 green) | finish the fix, tick D3, then D4 Moxfield |

**Moving the orchestrator between machines.** Worktrees are local; their branches are what travels. Hand off only at a package boundary: no implementer running, and every worktree clean (`git -C <wt> status --short` empty), because uncommitted work does not travel. Then move `main` and every `stream/*` branch to the other machine, and there run `git worktree add .claude/worktrees/stream-<x> stream/<x>` (no `-b`: the branch already exists). Tick-commits land on `main` from **one** machine at a time; the other stays read-only until the handoff.

### Geometry — re-ruled 2026-09-22 (user), superseding the ~9.75″ locked height and the camera-rotation requirement

**The camera stays landscape and unrotated. The operating height is 12″, and the accuracy sweep is 12″ and 20″. The 3×3 layout is laid out with its cards rotated**, long edge across the frame.

Why the old numbers changed — both were arithmetic, re-derived rather than assumed. Frame coverage at height *h* is `1920/(1360/h)` × `1080/(1360/h)` inches; the 3×3 footprint is 7.7″ × 10.7″.

1. **`CLAUDE.md` locks the mount at "about 9.75″" because that is where a 3×3 *first* fits. It fits with 0.04″ of margin — one millimetre.** That is a geometric floor, not an operating height: a card leaves the frame if the mat shifts. 10″ is only 0.24″. **12″ gives 1.83″.**
2. **The "1920 axis along the table's depth" requirement, and the 38%-resolution-loss warning behind it, silently assumed the 3×3 is laid out with its cards portrait** — footprint 7.7″ across × 10.7″ deep. Rotating the cards makes it 10.7″ × 7.7″, which fits a landscape frame at 12″ with 1.83″ to spare **at identical pixel resolution**: the card is 283×397 px either way, because px/inch is the same on both axes. So rotating the *layout* substitutes for rotating the *camera*, and the 38% loss never occurs. This is a third option the doc did not consider, and it is strictly simpler than twisting the camera.

Fit matrix, landscape, computed:

| Layout at 12″ | needs (across × deep) | result |
|---|---|---|
| 1 card | 2.5 × 3.5 | fits, +6.03″ |
| 3 in a line, running across | 10.7 × 2.5 | fits, +6.24″ |
| 3×3, cards portrait | 7.7 × 10.7 | **does not fit, −1.17″** |
| 3×3, cards **rotated** | 10.7 × 7.7 | fits, +1.83″ |

At 20″ every layout fits, portrait included (+5.18″ worst case, card 170×238 px — still inside SpellTable's proven 130–215 px range, which is the point of shooting it as the stress bound).

**Consequence — `ScanSettings.CameraRotationDegrees` = 0**, which closes the rotation-direction question C4 left open ("whether 90° or 270° is upright is decided at H1 mount time"). The property already accepts 0, so this is a composition-time value at I3 and **not** a frozen-surface edit. The `ScanSettings` default remains 90° and is not changed.

**Derive each fixture's height from its frame, not from a tape measure:** `height_inches = 1360 × 2.5 / card_pixel_width`. It runs through the real optics and hands E1 its "predicted X, measured Y" calibration for free. **Evidence this matters:** the 12 ad-hoc frames are recorded as shot at "~12″", but their ~260 × 370 px card implies **13.1″** from the width and 12.9″ from the height — both axes agree, and 13″ predicts 262×366 against the measured 260×370. The tape was an inch out; the `1360` constant is vindicated, not suspect. A mislabelled height does not fail loudly — it reads as mysteriously poor accuracy in B6's per-height table, which is why `capture-fixtures.sh` makes the label a validated argument.

**Fixtures are `.png`, not `.jpg`** (ruled 2026-09-22). The Stream C hardware harness saves a lossless PNG of the decoded BGR frame, and the camera's own MJPG artifacts are already baked into those pixels — re-encoding to JPEG to satisfy `test-images/README.md`'s stated extension would stack a second lossy generation on top for nothing.

**Follow-ups owed on `main`, not yet done:** `CLAUDE.md` §Geometry still states the ~9.75″ lock, the "1920 axis along the table's depth" requirement and the 38% warning; `docs/PLAN.md` may repeat them; and `test-images/README.md` still says `.jpg`. All three need correcting to match this ruling.

### Human track H (parallel, may start at G0)
- [x] **H1** 👤 Print the adjustable camera mount. Lock the height at about 9.75″ with the 1920 axis along the table's depth. — *done 2026-09-22. Mount printed and adjustable to any height in range. **Superseded in part by the geometry re-ruling below:** the height is not locked at 9.75″ and the camera is not rotated. Operating height **12″**, sweep **12″ and 20″**, camera **landscape, unrotated**.*
- [ ] **H2** 👤 Lighting and mat:
  - put the SAD lamp off-axis at a shallow angle
  - check a blank frame for PWM banding
  - try light, mid and dark mats, and record which one detects best
- [ ] **H3** 👤 Capture the fixture corpus:
  - heights **12″ and 20″** (re-ruled 2026-09-22, was 8/10/12/14/20 — see the geometry ruling below)
  - 1, 3 and 9 layouts. **The 9 layout is laid out with its cards rotated**, long edge across the frame — portrait does not fit at 12″
  - lands, normal cards and stretch cards
  - store them in the gitignored `test-images/fixtures/<height>in/<layout>/…jpg` (same relative path on the Mac and the PC; see `test-images/README.md`)
  - ground truth goes in `test-images/ground-truth.csv` with columns `file,height_in,layout,slot,oracle_name,rung,mat`
  - back it up outside git

  Use **`scripts/capture-fixtures.sh`** rather than copying frames by hand: it validates every label, refuses a height/layout
  combination that cannot physically fit, warns under 0.5″ of margin, and writes the ground-truth row only after the frame
  lands. `--dry-run` validates a whole shot list without touching the camera. Frames land as **`.png`**, not `.jpg` — see the
  ruling below.

  Gates B6.
- [x] **H4** 👤 A Moxfield account. Gates D5. *(user confirmed account ready 2026-09-22)*
- [x] **H6** 👤 A C920 attached to the Windows PC. Gates C4. — *confirmed attached 2026-09-21 (G0.3).*

### Stream A — UI · worktree `stream-a` · scope `src/LoreFetch.App/**`, `src/LoreFetch.Core/Trigger/**`, `Tests/StreamA/**`, README §A
Global overrides for every A brief:
- Build against the fakes only.
- `StubOracleCatalog` holds ~33k entries.
- Never compare a distance. Render `State` and `IsLowConfidence`.
- Never set `IsDefault` on any button.
- Status text comes from `IScanPipeline.SourceDescription`.

- [x] **A0** `AutoCaptureTrigger` in `Core/Trigger`: — *done 2026-09-21, `stream/a` `cdaf7f6` + fix `2cd4b05`. **Review found a real defect:** movement was measured frame-to-frame, so a card drifting 3 px/frame (under ε=4 each step, ~45 px over the window) fired the trigger on a moving hand. Now every snapshot is compared to the quads at the settle window's start, by max corner displacement (centroid is used only to match quads — a card rotated in place moves no centroid). "Scene breaks" = count ≠ expected, per stream-a; movement restarts the timer but never re-arms. TESTING.md §Unit still says "or movement beyond ε" re-arms — stale wording, superseded by stream-a A0. Consequence: swapping one card for another without lifting it does not re-fire auto mode; Space covers it. 14 tests; 6 chaos cases fail correctly.*
  - count-gated settle over `SettleMilliseconds`
  - movement epsilon is `MovementTolerancePixels`, matched by **nearest centroid**
  - "the scene breaks" means any count ≠ expected
  - re-arm only after the scene breaks, and `NotifyCaptured` suppresses re-firing

  Accept: every case in TESTING.md §Unit passes, including two near-equal-area quads swapping order without resetting the settle.
- [x] **A1** App shell and composition root: — *done 2026-09-21, `stream/a` `01b1fa6`. `AttachDeveloperTools` left uncalled: current Avalonia docs require portal licence credentials (paid tier). Smoke-launch via `LOREFETCH_SMOKE_EXIT_MS=3000` independently re-run: starts in Fakes mode against generated frames in `%TEMP%`, exits 0. A leaked temp folder per launch was found; the first fix (`d5b0b2d`) still leaked on the smoke path, because Avalonia's `Shutdown()` never raises `ShutdownRequested` (verified in the 12.1.2 source). The fix in `bcfe815` disposes on `Exit`, idempotently; re-verified 0 → 0 folders across a smoke launch.*
  - `AppComposition` offers a `Fakes` mode, with `Real` added at integration
  - it calls `ScanPipelineFactory.Create` and `IFrameSourceFactory.CreateAsync`, then `RunAsync` once
  - a status line
  - it resolves `AttachDeveloperTools` licensing in its first hour, and leaves it uncalled if the tool turns out to be paid

  Accept: the app launches against `FolderFrameSourceFactory`.
- [x] **A2** Preview: — *done 2026-09-21, `stream/a` `6b47e28` + fix `22d373c`. **Review found a data race:** the render callback took a reference to the one shared staging buffer and converted it after releasing the lock, so the pipeline thread could overwrite it mid-conversion (torn frames, hidden by the slow fake source). Now a three-slot `FrameHandoff` guarantees the producer never writes the slot being converted. Its 20,000-frame uniformity test fails 20/20 when the rotation is collapsed, and an independent chaos case (the consumer never marks its slot taken) fails 5/5. The ~15 fps gate used to drop a frame arriving inside the interval, so the last frame before a stall was never drawn; it now defers with a timer, still at most one render outstanding.*
  - one `WriteableBitmap` for the life of the app
  - lock, copy and dispose the lock every frame
  - BGR→BGRA conversion that switches on `Layout` and respects `RowBytes`
  - `InvalidateVisual()` on the `Image`
  - coalesce via `RequestAnimationFrame`, throttled to about 15 fps
  - copy inside the `FrameProcessed` handler

  Accept: unit tests for the pure `PixelConvert` function (BGR24/BGRA32 with padded strides).
- [x] **A3** Quad overlay: vector children over the `Image`, and a pure frame→control transform that handles `Uniform` letterboxing. Accept: transform unit tests with rotated (1080×1920) geometry. — *done 2026-09-21, `stream/a` `8d2c30c`. A pure `FrameToControlTransform` (scale = min, centred offsets) and `Polygon` children on a hit-test-invisible canvas. Independent chaos: swapping the X/Y offsets fails all 3 transform tests. **The smoke launch caught a crash no unit test could:** `RequestAnimationFrame` was called from the pipeline thread and asserts UI-thread access; the call is now marshalled through `Dispatcher.UIThread.Post`.*
- [x] **A4** ∥ Expected-count selector (1, 3 or 9) and an auto-capture toggle, both written through a view model to `ScanSettings`. *(stream/a `d30d7bd` + `163c2e9`; first view model `MainViewModel` writes through to `ScanSettings.ExpectedCount`/`AutoCaptureEnabled`, 3 radio buttons no `IsDefault`, `LOREFETCH_FRAMES_DIR` with graceful fallback + logged error. 44 StreamA + 143 Integration green (Mac filter); smoke-launched exit 0; chaos found + closed an `IsImageFile` rejection-branch test gap. **Screenshot PNG is `WindowsOnly` — headless `CaptureRenderedFrame` returns null on macOS ARM64, so pixel verification is deferred to the PC leg; a cross-platform visual-tree test runs on Mac.**)*
  **Added 2026-09-22 (user ruling):** a `LOREFETCH_FRAMES_DIR` environment variable. When set, Fakes mode's `FolderFrameSourceFactory` reads that folder instead of `DemoFrames`' generated rectangles, so the user's real captures in `test-images/ad-hoc/` (12 × 1920×1080, 4 cards × black/brown/white surfaces, ~12″) cycle through the preview. Unset → current behaviour. A missing or empty folder is a clear logged error, not a crash. The images are gitignored and never copied into the repo.

**UI screenshots, standing from A4 onward (user ruling 2026-09-22).** The user wants to *see* the UI run, not only read test counts. Each of A4–A9 adds at least one `Avalonia.Headless` test that renders the window to a PNG — driving Space/Enter/Escape where the package is about keys — and writes it to the test's output directory, **never into the repo**. The orchestrator reads every PNG during verification and sends it to the user with `SendUserFile`. Where it helps, the orchestrator also smoke-launches the real exe with `LOREFETCH_FRAMES_DIR` and `LOREFETCH_SMOKE_EXIT_MS`, captures the screen, and sends that too. Neither replaces **A10**, which stays the user's hands-on keyboard run. The orchestrator has no desktop-control tools, so it cannot click a live window.
- [x] **A5** Cohort grid: a tile view model wraps `CohortTile` and raises INPC. It has the four state visuals plus a low-confidence highlight. Accept: view-model unit tests for each state. *(stream/a `6dccb9a`; `TileViewModel : ObservableObject` wraps `CohortTile` — reflects State/DisplayName(blank when Unresolved)/DistanceText(blank when null)/IsLowConfidence directly, never re-derives low-conf from ChosenDistance; `Refresh()` re-notifies for A6; exposes `Tile` so A6 reaches the mutators. `MainViewModel.Tiles` + `LoadCohort(Cohort)` (A7 wires capture). MainWindow.axaml cohort grid: ItemsControl+WrapPanel, four state visuals + amber low-conf border; ✕ marker IsVisible→ShowExcludedMarker. 18 new tests (62 StreamA total); cross-platform visual-tree check + WindowsOnly PNG. Verified: scope App+StreamA only, build 0 err, StreamA 62 pass, Integration 143 pass/8 skip, smoke-launch exit 0. Un-briefed chaos: inverted ShowExcludedMarker → visual-tree test fails Assert.Single (4 markers) at line 81 — ✕ binding is real, not vacuous.)*
- [x] **A6** Tile interactions: left-click calls `ToggleExcluded`, and the context menu offers `SetManually` and `Clear`. The type-ahead uses these `AutoCompleteBox` settings:
  - `AsyncPopulator`, filtering off the UI thread with `Take(20)`
  - `MinimumPrefixLength` of 2
  - `MinimumPopulateDelay` of 150 ms
  - ordinal matching
  - runners-up listed first
  - *(stream/a `0368a35`; `TileViewModel.ToggleExcludedFromUi/SetManuallyFromUi/ClearFromUi` each = tile mutator + `Refresh()` (UI never assigns State/Chosen). `TypeAheadPopulator` built by `BuildPopulator`: yields off UI thread, honours cancellation, runners-up (`CohortTile.Candidates`) first then `IOracleCatalog.All`, `StartsWith(…,Ordinal)`, deduped by OracleId, capped `Take(20)`; `MinimumPrefixLength=2`/`MinimumPopulateDelay=150ms` set in code-behind `OnTypeAheadLoaded`. `IOracleCatalog` threaded via optional params (AppComposition builds `StubOracleCatalog()` 33k → AppSession → MainViewModel.LoadCohort → TileViewModel); all A4/A5 ctors still compile. New `CatalogItem` (App wrapper, `ToString()`=OracleName) avoids touching frozen Core. MainWindow: `Tapped`→toggle, `ContextMenu` (Set manually…/Clear), `AutoCompleteBox` overlay. **Override applied (V16): the doc's "few hundred entries / can't test 33k" is stale — stub is now 33k, cap tested against it.** No keyboard/capture (A7). 17 new tests (79 StreamA). Verified: scope App+StreamA only, build 0 err/0 warn, StreamA 79 pass, Integration 143 pass/8 skip, smoke-launch exit 0. Un-briefed chaos: catalog `StartsWith`→`Contains` → substring test fails at line 273 ("Lightning Bolt" leaks on prefix "Bolt") — prefix semantics genuinely enforced.)*

  Accept: a populator unit test against 33k entries (capped, cancellable).
- [x] **A7** Keyboard map:
  - a window-level `Tunnel` handler, which returns without setting `Handled` when focus is in a `TextBox`
  - Space awaits `CaptureAsync`
  - Enter commits and keeps the cohort on `CollectionStoreException`
  - Escape discards
  - keys 1–9 are optional

  Accept: `Avalonia.Headless.XUnit` tests cover Space, Enter and Esc, and check that **a space typed into the type-ahead still arrives**.
  - *(stream/a `7b3accb` + test `97615f3`; window-level `AddHandler(KeyDownEvent, …, Tunnel)` — focus bail (`FocusManager.GetFocusedElement() is TextBox` → return without `Handled`, the WM_CHAR trap), no `IsDefault` button. `MainViewModel.CaptureFromPipelineAsync` runs `CaptureAsync` off-thread and marshals `LoadCohort` back (no-op on null/0 detections); `CommitCohortAsync` no-op when store/cohort null; `ClearPendingCohort`/`HasPendingCohort`. Enter handler clears **only on success**; catches `CollectionStoreException` and keeps the cohort (retry banner deferred to A9); `AutoCaptured` subscribed + marshaled. `ICollectionStore` threaded via optional params (`StubCollectionStore` in `CreateFakesAsync`). 1–9 toggles: see report. All A4–A6 ctors still compile. Verified: scope App+StreamA only, build 0 err, **StreamA 87 pass**, Integration 143 pass/8 skip, smoke-launch exit 0. Un-briefed chaos found the retain-on-failure path had **no test** (clearing in the catch left all 86 green) → closed by `97615f3`; re-ran the chaos against the new test and it fails `Expected 2, Actual 0` at line 190, right reason.)*
- [x] **A8** Collection view and export: a sortable `DataGrid` over `ListAsync` (keep the DataGrid theme include), and an exporter picker with an `IsVerified` badge. No per-format code: `grep -ri moxfield src/LoreFetch.App` must return nothing. *(stream/a `4c7426a`; `CollectionViewModel` (Rows from `ListAsync`, `ExporterItems`, testable `ExportToStreamAsync(exporter, stream, ct)`), `ExporterItem` wrapper exposing `IsUnverified` for the badge. MainWindow: `DataGrid x:Name=CollectionGrid` (sortable, 6 cols) + `ListBox x:Name=ExporterList` with a `Border IsVisible={Binding IsUnverified}` "unverified" badge; DataGrid Fluent theme include kept. Exporters threaded via optional `AppSession.Exporters` (`CreateFakesAsync` wires two `StubCollectionExporter`s — one verified, one not — to exercise the badge; real Native+Moxfield wired at integration). Export uses `StorageProvider.SaveFilePickerAsync`→stream→`ExportToStreamAsync`; UI has zero per-format code. Verified: scope App+StreamA only, build 0 err/0 warn, **StreamA 91 pass**, Integration 143 pass/8 skip, `grep -ri moxfield src/LoreFetch.App` empty, smoke-launch exit 0. Un-briefed chaos: forced badge `IsVisible=False` → visual-tree test fails `Assert.NotEmpty (empty)` at line 162 — badge binding + tree walk are load-bearing. Non-happy states (empty placeholder, store-locked banner, source-failed) deferred to A9.)*
- [x] **A9** Non-happy states:
  - empty collection
  - no frame source
  - `SourceFailed`
  - a store-lock retry
  - a missing index or thresholds file

  No stack traces in the UI.
  - *(stream/a `ac55852`; empty-collection placeholder (`CollectionViewModel.IsEmpty`/`HasRows`), `SourceFailed` banner (subscribed + marshalled, `.Message` only, unsubscribed on close), store-lock retry banner on `CollectionStoreException` from commit/export with a Retry that re-attempts and clears on success (cohort retained — builds on A7), startup-error surface via `MainViewModel.StartupErrorMessage` string (composition sets it — **no contract change; STOP not triggered**), and a visible "Set card manually…" hint on `Unresolved` tiles. No stack traces in any surface. Verified: scope App+StreamA only, **StreamA 106 pass/0 fail** (2 WindowsOnly screenshots excluded), Integration 143 pass/8 skip, `grep -ri moxfield src/LoreFetch.App` empty, smoke-launch exit 0. Un-briefed chaos: `ex.Message`→`ex.ToString()` in the SourceFailed handler → no-stack-trace test fails on "System." at line 213 — the invariant is real. Adjacent find: 9 pre-existing `xUnit1051` analyzer warnings in A6's `TileInteractionTests.cs` (non-blocking).)*
- [ ] **A10** 🧭👤 Done-when run on the Windows PC:
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
- The Scryfall image cache lives **outside the repo**: `C:\LoreFetchData\scryfall-cache` on the PC, `~/LoreFetchData/scryfall-cache` on the Mac. Take it as a parameter, never a constant.
- Golden tests carry `[Trait("Category","WindowsOnly")]`.

- [x] **B1a** Hash core in `Core/Imaging`: — *done 2026-09-21, `stream/b` `d4badaf`. Measured on win-x64 (bound in brackets): two input scales 4 (≤100); brightness ±30 → 14/7 (≤60); gamma 1.3/0.7 → 17/19 (≤120); inverted 1002 (≥900). 29 tests; all 4 briefed chaos cases fail correctly. **Independent finding:** switching step 5 to `INTER_LINEAR` leaves all 29 green — the invariant tests cannot see an unintended filter change, which is exactly what B1b's goldens must catch; carried into B1b's brief as a named chaos case.*
  - `ReferenceTransform.Prepare`: GaussianBlur 3×3 with σX = σY = 1 given explicitly, then resize to 96 px wide `INTER_AREA`, then grayscale
  - `QueryTransform.Prepare`: grayscale only
  - the **single** shared `CardHasher.Hash(gray)` for steps 4–6: region `w×0.85w`, then 32×32 `INTER_AREA`, then per cell the **upper order statistic (33rd of 64)** with the tie-break `v == m && m > 128`, packed one `ulong` per cell
  - Hamming distance via `PopCount`

  Accept, as bounds rather than equalities: two input scales stay within a bound, brightness and gamma shifts stay within a bound, an inverted image lands at ≈1024, and the median and tie-break unit tests pass.
- [x] **B1b** Golden hashes: deterministic procedural inputs, with the expected hex committed after it is generated **on win-x64**. Accept: the test passes on Windows and is filtered out on macOS. — *done 2026-09-21, `stream/b` `3d28cea`. 8 goldens, traited `WindowsOnly`: 45 run unfiltered, 37 under `Category!=WindowsOnly`, which `scripts/lorefetch.sh` applies on the macOS leg. Independent chaos: switching step 5 to `INTER_LINEAR`, which B1a's 29 invariant tests could not see, now fails all 8 goldens.*
- [x] **B3a** Index format `cards.lfidx`: — *done 2026-09-21, `stream/b` `96d0f52`. Magic `LFIX`, version and counts; a deduplicated oracle table; little-endian, length-prefixed UTF-8. Written by one implementer, which was cut off by the usage limit mid-chaos with a UTF-16 mutation left in the file (reverted by the orchestrator); finished and chaos-verified by a fresh one. 3 chaos cases fail correctly.*
  - header: magic, version, counts
  - per entry: 16 `ulong` hash values, an oracle index, the `ArtworkId`, and an `IsBasicLand` flag (V11)
  - an oracle name table
  - a reader and a writer

  Accept: round-trip tests on a synthetic index.
- [x] **B3b** `HashCardIdentifier`, which implements `ICardIdentifier` and `IOracleCatalog`: — *done 2026-09-22, `stream/b` `ed4d4f0`…`1b4f8d3`. Flat `ulong[]` scan, full 1024-bit distance, a 180° flip of the full grayscale card before the crop, best per oracle, and a bounded top-K with a `(Distance, oracle-table index)` tie-break. **Timing: 3.1–3.9 ms/query, ~28–35 ms for 9 queries** against a realistic 48,700-entry / 33,600-oracle fixture. **Review found the first version at 11.7 ms/query:** a full `Sort` of every oracle on each query took 8–12 ms of it, while the scan took ~2. StreamB now has 63 tests; all 6 briefed chaos cases fail correctly. **Two independent chaos cases:** a mirror flip (`FlipMode.Y`) fails 3 tests. Having the top-K displace against the *best* kept candidate instead of the worst **left all 61 tests green**, and is now caught by a targeted test plus a fixed-seed property test against a naive full-sort reference. **Harness pitfall the implementer hit:** `dotnet test --no-build` reuses a stale `Core.dll`, so a mutation looks caught-by-nothing. Always rebuild when chaos-testing.*
  - brute-force search
  - best distance per `OracleId`
  - hashes both 180° orientations and keeps the better one
  - never applies a threshold

  Accept: ranking and distinctness tests, plus a timing test for 9 queries against a synthetic 50k index (log the time; soft-fail above 50 ms).
- [x] **B4a** ∥ Lab `bulk` command: — *done 2026-09-22, `stream/b` `2ea5e47`…`9d5cb82`. Live cascade (2026-09-22 file): 54,773 / 37,740 raw → 54,761 image_status → 54,360 en → 50,920 with `image_uris.normal` (3,440 multi-faced skipped explicitly) → 48,936 layouts → **48,750 arts / 33,612 oracle ids**; `frame == "2015"` would leave 31,720 (information only, not applied). Manifest `scryfall-bulk/filtered-artworks.jsonl` (gitignored) with a public reader for B4b/B4c. StreamB 63 → 81 tests; 3 briefed chaos cases fail correctly. **Independent chaos:** counting arts instead of distinct oracle ids fails 2 tests. Lab production code is 717 lines, reported against the tripwire: it covers an HTTP client, two parsers, the cascade, manifest I/O and two commands.*
  - **Open question answered — single-printing fraction: 60.07%.** 21,032 of 35,013 in-scope illustration ids have exactly one in-scope printing (63,373 printings in `default_cards`). Filter: `lang == "en"` && `finishes` contains `nonfoil` && has top-level `image_uris` && `frame == "2015"` && the same layout/set_type exclusions as the cascade. Where the art is unambiguous, the art match is the printing, so printing resolution for ~60% of arts is a plausible v1.5 feature.
  - `GET /bulk-data/unique_artwork` with the required `User-Agent` and `Accept` headers
  - download `jsonl_download_uri` to `scryfall-bulk/` (gitignored)
  - apply the filter cascade from stream-b §B4 and print the count at each step

  Accept: a unit test over a small committed JSONL sample (text only) reproduces the filter decisions.

  **Open question, deferred here from the `ArtworkId` ruling (2026-09-21):** what fraction of in-scope artworks have exactly **one** in-scope printing (English, non-foil, single-faced, modern frame)? Where the art is unambiguous, the art match *is* the printing — which is what would make accurate prices, and importers needing a set or printing id such as ManaBox, possible. Needs `default_cards` in addition to `unique_artwork`. **This decides whether printing resolution is a v1.5 feature, not whether to keep the id** — that was settled independently, because keeping it is justified at any value of this number. Report the fraction and the exact filter used.
- [x] **B4b** Lab `images` command: download `image_uris.normal` into the external cache. It resumes, runs with polite concurrency, respects HTTP 429, and skips files already present. — *done 2026-09-22, `stream/b` `ab1e9f6`. Writes `<cache>/<ArtworkId>.jpg` via temp-then-move. Refuses a cache inside the repo and any URL without `/normal/`. Checks JPEG magic bytes and logs sizes other than 488×680. StreamB 81 → 95 tests; 4 briefed chaos cases fail correctly. **Independent chaos:** disabling the non-`normal` refusal fails 1 test. Smoke: 50 images in 0.8 s (~59/s), and the rerun skipped all 50 without a request. **Defect for B4c to fix:** `RepoPaths.FindRepoRoot` walks up from the exe, so a Lab build copied out of the repo crashes with an unhandled exception that also names the wrong flag (`--out`).*
- [x] **B4c** Lab `build-index` command: `ReferenceTransform` → `CardHasher` → `cards.lfidx`. It supports a `--subset N` fallback that labels the index size. — *done 2026-09-22, `stream/b` `1394308`. Output is in manifest order whatever the parallelism, so rebuilds are byte-identical. The write is atomic and re-read to verify. A missing image fails the build unless `--allow-missing` is given. `--subset N` takes the first N entries; `--ids <file> [--fill N]` exists for B5b's four-card smoke index. Writes a `<out>.build.json` sidecar. Also fixed B4b's `RepoPaths` defect. StreamB 95 → 133 tests; 4 briefed chaos cases fail correctly, including the query transform swapped in for the reference transform. **Independent chaos:** hardcoding `IsBasicLand = false` fails 1 test. The implementer's spot check put 20 random renders through the query path: **20/20 own `ArtworkId` at rank 1, distances 10–36**. That is a preview of B2's floor, not the gate itself. **Note:** B3b's timing test soft-skips (over 50 ms) when the full suite runs in parallel with the CPU-heavy build-index tests. That skip is by design, but the number is only meaningful from an isolated run.*
- [x] **B4d** 🧭👤 Run B4a–B4c in full on **the win-x64 PC** (a 👤 ask: the orchestrator is on the Mac) (about 6 GB, hours). Commit `data/index/cards.lfidx` and record its artwork count and SHA-256 here. If it stalls, use a labelled 5k subset. — *done 2026-09-22 by the orchestrator on the PC, `stream/b` `0412abe`. Inputs: the 2026-09-22 `unique_artwork` file, and 48,750 `normal` renders in `C:\LoreFetchData\scryfall-cache` (5.1 GB, 0 failures, 10.7 min at 76/s — minutes, not the hours estimated). **Full index, no subset: 48,750 artworks / 33,612 oracle cards / 1,882 basic lands, 10,460,648 bytes, SHA-256 `6495314eb3e5f37e3c1bd5830aa506d6efc617e23303c46f87851edef09e4dd3`,** built in 40 s. An orchestrator rebuild from the same inputs was byte-identical. The committed blob's SHA matches, and git detects the file as binary. **Size is ~10.0 MiB, not the ~8.2 MiB predicted:** the prediction left out the per-entry 36-character `ArtworkId`, about 1.9 MB. **Recommend adding `*.lfidx binary` to `.gitattributes` on `main`:** `core.autocrlf` is on and nothing yet pins the file as binary except git's NUL-byte heuristic.*
- [x] **B5a** ∥ `ContourCardDetector` in `Core/Imaging`: — *done 2026-09-22, `stream/b` `53f0c10`…`8d3746e`. Settings: min area 1% of the frame, border margin 2 px, Canny 50/150, a 5×5 close, `approxPolyDP` ε = 2% of the perimeter. Corner ordering puts a short edge first, so the warp comes out upright or 180°, never at 90°. `DetectWithDiagnostics` exposes every rejection and its reason. StreamB 133 → 157 tests. Of the 5 briefed chaos cases, 3 initially left tests green: min-area, ascending order and nested dedupe. The implementer strengthened the tests until all 5 fail. **Nested dedupe cannot be reached end-to-end**, because `RETR_EXTERNAL` never returns inner contours; it is covered by a direct unit test. **Independent chaos:** disabling the border-touch rejection fails 1 test.*
  - **Real capture, 10/12 frames with exactly one detection** (quads 239–283 × 347–393 px). Misses: `atarka_foil_black` (foil, out of scope) and **`verix_sleeved_black`**: the sleeve's outer outline approximates to more than 4 points, and the card inside is not found. Both are recorded as known misses in the test, not tuned away. **Plains on black, the Risk 4 case, is "detected", but by eye the quad sits on the *inner* edge of the black border, not on the card's outer edge.** Black-on-black leaves no outer edge. So Risk 4 shows up as a **crop-scale error (roughly 5–10% per side)** rather than a miss, and "exactly one detection" cannot see it. Sol Ring on white lands on the true outer edge. **B5b's rectified crops and B5c's crop-scale curve must look at this directly.** Overlays are in `C:\LoreFetchData\detect-out\`.
  - Canny → morphological close → `findContours` External → `approxPolyDP` to 4 points → order corners TL, TR, BR, BL
  - aspect 1.397 ±15% (the ±25% widening is configurable) and a minimum area
  - top N by area
  - every discard reason logged

  Accept, on generated frames: an empty mat on light, mid and dark backgrounds returns 0; a partial grid returns the correct count; a hand-shaped blob is rejected; rotated cards come back correctly ordered.
  **Added 2026-09-22 (user ruling): real-capture check.** `test-images/ad-hoc/` holds 12 real C920 frames: Sol Ring (clean normal card), Verix Bladewing (sleeved), Atarka (foil, out of scope) and Plains (land). Each is shot on black foam, brown cardboard and white paper at ~12″, with a cluttered desk in frame (deck box, power bricks, controller, card stacks). The measured card is ~260×370 px against a predicted 283×396. An artifact-gated test in `Tests/StreamB` expects **exactly one** detection per frame and skips with a reason when the folder is absent. **Plains on black foam is the Risk 4 worst case** (black border on a black mat): if it misses, record the fact rather than tuning until it passes. Also a Lab `detect <image> --out <png>` command that draws accepted quads, rejected contours with their discard reason, and the rectified crop (after B5b). It writes to a path outside the repo. The orchestrator reads each PNG and sends it to the user, so the user sees detection working on their own photos before integration.
- [x] **B5b** `PerspectiveRectifier`: warp to 488×680 with `INTER_LINEAR` pinned (full item text below the results). — *done 2026-09-22, `stream/b` `ebc7a14`…`9679148`. Corners map to pixel centres (0,0)…(487,679), with `INTER_LINEAR` and `Replicate` passed explicitly. StreamB 157 → 163 tests. A synthetic detect → rectify → hash round trip lands at Hamming 17. 4 briefed chaos cases fail correctly (Nearest, mirror, off-by-one size, channel swap). **Independent chaos:** switching to the edge convention (488 instead of 487) fails 2 tests. Adds a Lab `identify <image>` command: detect → rectify → identify against the full committed index, printing the top 5.*
  - **Early real-path smoke, full 48,750-art index, the 12 ad-hoc frames at ~12″:**

    | Frame | Rank 1 (distance) | Rank 2 (distance) | Margin |
    |---|---|---|---|
    | solring_black | **Sol Ring ✓ (94)** | Thraben Charm 159 | 65 |
    | solring_brown | ✗ Mantle of the Ancients 147 | Sol Ring 157 | — |
    | solring_white | Sol Ring ✓ 163 | Mantle of the Ancients 185 | 22 |
    | plains_black | ✗ Mountain 345 | Personal Incarnation 351 | — |
    | plains_brown | Plains ✓ 143 | Life Goes On 253 | 110 |
    | plains_white | Plains ✓ 168 | Tarnation Vista 259 | 91 |
    | verix_sleeved_brown | Verix Bladewing ✓ 242 | Null Champion 255 | 13 |
    | verix_sleeved_white | Verix Bladewing ✓ 257 | White Ward 287 | 30 |
    | atarka_foil_brown / white | Atarka ✓ 131 / 133 | Skyshaper 248 / Nether Spirit 252 | 117 / 119 |
    | atarka_foil_black, verix_sleeved_black | not detected (B5a) | | |

    **Sol Ring's distance is 94** (black mat), matching `ArtworkId` `073bfdca-d7b8-4f4b-93f3-6e7c44bc0b0a` (SCD). The implementer compared the crop and the render by eye and judged it the right art. **Tally: 8 correct at rank 1, 2 wrong at rank 1, 2 not detected.** Real photos land at 94–257, against 10–36 for render-to-self, which leaves a wide gap for lighting and focus to close (Risk 1). Margins are thin on Sol Ring and on the sleeved Verix. **Plains on black was confirmed by its crop:** the black border is absent from the rectified card (about 5% inset per side in width, 3–4% in height), and the result is a confident wrong match. Sol Ring on black shows no inset. The failure is this printing's border merging into the mat, not black mats in general. This is B5c's evidence. One frame per cell, so none of this is an accuracy number; B6 still needs H3.

  Item text: Accept: a known quad on a synthetic frame produces the expected corner pixels. Extends the B5a Lab `detect` command to write each rectified 488×680 crop from the ad-hoc frames next to its overlay PNG, for a by-eye check.
  **Early real-path smoke (user ruling 2026-09-22):** once B4c exists, build a small labelled subset index that includes those four cards' arts. Sol Ring should then retrieve its own `ArtworkId` through detect → rectify → identify. Record the distance. This is a smoke check, not an accuracy number: B6 still needs the H3 corpus.
- [x] **B7** Lab synthetic generator:
  - px/inch = 1360/height
  - keystone, blur, noise and JPEG artefacts
  - a bare-mat generator
  - it **calls the shipping detect → rectify → hash path**
  - CI uses procedural card-like images and an index built inside the test (V13)

  Accept: an integration test in `Tests/StreamB` confirms that a generated card at 9.75″ retrieves its own artwork.

  *Done 2026-09-22, Mac, `stream/b` `abe9ac8` + `eaf07dc` + `d520d29`. `SyntheticFrameGenerator` (downscale `INTER_AREA` → keystone → mat composite → blur → Gaussian noise → JPEG round trip), `BareMatGenerator` at light/mid/dark, and a `lab synth card|mat` command refusing in-repo output. Noise and JPEG use a seeded `System.Random` rather than OpenCV's RNG, for cross-platform determinism. StreamB 163 → **184** (183 passed, 1 soft-skip); Integration unchanged at 143/8.*

  **Round trip, procedural in-test index (V13), both heights:** 9.75″ rank-1 **64** / rank-2 103; 12″ rank-1 **68** / rank-2 113. Probing all 24 in-index seeds gave **24/24 correct@1** at distances 40–102. The 12″ case was added after the geometry re-ruling; note 12″ costs 4 bits of distance but *gains* 6 bits of margin, so moving the operating height there is free.

  🔴 **Two review rounds, both on reviewer-checklist item 2 ("`INTER_AREA` at every resize, including inside the synthetic generator") — and this is the pattern's third and fourth appearance in stream B.** The implementer's own briefed chaos case reported honestly that swapping the generator's downscale `INTER_AREA`→`INTER_LINEAR` **left all 179 tests green**; sent back and closed with a *differential* pin (`SyntheticFrameOptions.DownscaleInterpolation`, default asserted + a different value must change the pixels) rather than a golden, because only the filter *choice* is at risk here and a differential pin is architecture-independent, where a golden would have to be `WindowsOnly` and generated on the PC. Then **orchestrator chaos on a spot no brief named** found the same hole one step down: keystone warp `Linear`→`Nearest` **left all 182 green**. Closed the same way (`KeystoneInterpolation`). Both pins were verified to actually reach their `Cv2` calls rather than merely being declared.

  **`BorderTypes.Replicate` was examined and deliberately *not* pinned, with reasoning** — the right outcome, not a miss. The composite mask is filled from the destination quad's own geometric corners, and the keystone inset only pulls corners inward, so a border mode can only supply pixels strictly outside the true quad — exactly the region the mask always excludes. A border-mode change is therefore structurally unobservable downstream, not merely untested; swapping to `BorderTypes.Constant` left all 184 green for a provably different and non-actionable reason. Recorded inline at the call site instead of pinned.

  **Independent chaos beyond the brief:** dropping the JPEG round trip was caught by exactly 1 of 179 tests, confirming that test is non-redundant. The standing practice this package produced is now in `docs/TESTING.md` §*pin every interpolation flag and border mode*. Lab production code is ~1,504 lines total, reported against the tripwire and accepted — it is a multi-command dev harness, not one package doing several jobs.
- [x] **B2** Round-trip gate. It runs locally and is artifact-gated on the external cache and the committed index: Scryfall render → `RectifiedCard` → `Identify`. It asserts that rank 1 has the **same `ArtworkId`** and a distance ≤ the recorded floor bound. It writes `referenceFloor` and the margin to the best different artwork into `thresholds.json`. It uses a fixed sample, for example 200 renders spread across the ladder. **If the rank-1 artwork rate is below 99%, stop and ask.** Record the floor here.

  *Done 2026-09-22, **Mac (arm64-darwin)**, `stream/b` `ce5d407`. Ran against the full 48,750-artwork committed index and the complete real cache (48,750 renders re-pulled on the Mac from the ported manifest in 738 s at 66/s, 0 failures). Fixed sample of 200, seed `20260922`. StreamB 184 → **197**; Integration unchanged 143/8. `data/index/thresholds.json` written at the path Stream 0 already froze, with `goodDistance`/`okDistance` deliberately absent so `ThresholdsFile.Load` throws "missing required field" until B6 sets them — loud, not a fabricated placeholder.*

  | Measure | Value |
  |---|---|
  | **referenceFloor** | **55** (own distance min 9, mean 22.3, median 21) |
  | Margin to best *different* artwork | min **63**, mean 204.1, median 203, max 322 |
  | **Rank-1 `ArtworkId` rate** | **100.00% (200/200)** — lands 20/20, non-lands 180/180 |

  The worst margin (63) exceeds the floor (55), so every sampled artwork is separated from its nearest impostor by more than the whole spread of own-distances. Far above the 99% stop-and-ask bar. **Marked `provisional: true` / `measuredOn: arm64-darwin`** per Machine split rule 4; the PC promotes it. The thresholds file now records its own provenance (architecture, OS, index SHA-256, sample size, seed), which turns rule 4 from a rule someone must remember into a mechanism.

  🔴 **The gate has a structural limit, found by the implementer's briefed chaos case and worth understanding before anyone trusts it further than it goes.** Swapping the query transform for the reference transform on the query side **did not fail** the rank-1 or bound assertions — the reported distance *improved to 0*. Cause: the queried render's own index entry was built by that same reference pipeline from the same bytes, so forcing the two sides into symmetry only makes a self-match better. **A render-self-match gate is therefore biased toward passing when the two sides converge, even wrongly, and cannot by itself guard reference/query divergence.** Closed as far as it can be by cross-checking `Identify`'s reported distance against an independent raw-scan distance (catches the mutation: expected 17, actual 0). The real guards on divergence remain B1b's goldens and B2's new witness — which is what CLAUDE.md's "golden hashes **plus** a round-trip test" already implies; the risk was reading the round-trip gate as doing more than it does.

  **Query-hash witness (orchestrator addition to the brief).** 50 query-side 1024-bit hashes for a fixed seed, committed as text. **Its trait choice is better than the brief asked for:** rather than copying B1b's `WindowsOnly`, it reads the runtime architecture and compares against the witness's recorded `measuredOn` — same architecture asserts bit-exact, foreign architecture skips with the measured bit-difference count in the message. That matters concretely: `scripts/lorefetch.sh` filters `WindowsOnly` out on macOS, so the goldens are *not* run in a script-driven Mac run, while the witness is. **Orchestrator chaos confirms it:** mutating `QueryTransform`'s `BGR2GRAY` to `RGB2GRAY` is caught by exactly two tests — a golden and the witness — and the witness is the one that survives the macOS filter.
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

- [x] **C1a** Internal stage, channel and pooling: newest-frame-only, disposal on drop, and `InvalidOperationException` on a second enumerator. Accept: under a slow consumer, memory stays bounded and every buffer is returned (counting pool). — *done 2026-09-21, `stream/c` `f4a459f` + fixups `84979d7`. Review sent back 4 `xUnit1051` warnings and an enumerator test that *hung* rather than failed when its guard was removed; both fixed, and the test is now bounded by `WaitAsync`. Also replaces StreamC's placeholder with real internal-touching tests, closing the `InternalsVisibleTo` thread.*
- [x] **C1b** Decode and rotate: `Cv2.ImDecode` into a pooled BGR24 `CameraFrame`, then `Cv2.Rotate` using the rotation read once at open. `Geometry` is post-rotation. Decode time is logged. Accept: tests on synthetic JPEGs (encoded in memory) at 0, 90, 180 and 270 degrees. — *done 2026-09-21, `stream/c` `1f8380b`. Independent chaos: swapping the 90°/270° mappings fails the marker-corner test at both angles.*
- [x] **C1c** Watchdogs: `FirstFrameTimeoutMs` and `FrameWatchdogMs` raise `FrameSourceException` naming the three causes. Accept: tests with a fake clock or short timeouts. — *done 2026-09-21, `stream/c` `72503b5` + `8382f25` + `56b1b9b`. Generic over `IAsyncEnumerable<T>` so C2 can wrap either stage; injectable `TimeProvider`. The finishing implementer found a real bug: disposing an async iterator while its `MoveNextAsync` is pending throws `NotSupportedException`, so disposal is deferred until the pending call settles. Review strengthened two tests. The healthy stream now spans six frame-timeout periods, because at one period the only-reset-once regression was caught in just 3 of 5 runs; it is now 5 of 5. And using the first-frame timeout for mid-stream gaps passed all 18 tests, so the stall test now pins both the detection time (< 2 s) and the timeout value in the message.*
- [x] **C2** FlashCap shim and `WebcamFrameSourceFactory : IFrameSourceFactory`:
  - enumerate and log every descriptor with its backend
  - zero descriptors gets its own diagnosis
  - select 1920×1080, `PixelFormats.JPEG`, `(double)fps >= 30`, or throw with the full list
  - backend preference DShow, then MF, never VfW
  - a backend-prefixed `PreferredDeviceId`
  - `Description` reports what was negotiated

  Accept: unit tests of the selection logic over plain descriptor data objects.

  *Done 2026-09-22, Windows PC, `stream/c` `1f60ec0` + fix `cd53e54`.* 37 StreamC tests; the implementer's five chaos cases (`==` for `>=`, VfW in the preference list, swapped preference, first-characteristic fallback, hardcoded `Description`) each failed the right test.
  🔴 **Review caught a defect every unit test passed: the device was opened but never `StartAsync`-ed.** FlashCap delivers nothing to an un-started device, so every real open would have ended in the 10 s first-frame timeout, misdiagnosed as unplugged / in use / permission denied. It is not unit-testable (FlashCap devices cannot be faked from outside its assembly), so the C4 hardware harness is its guard. The fix also releases the device if anything throws after `OpenAsync`, and logs time-to-first-frame.
- [x] **C3** ∥ README §C (including the macOS compile-only note). *(stream/c `7bf2632`; README-only. §C covers the FlashCap→`IFrameSource` pipeline (`WebcamFrameSourceFactory` entry point, DropOldest single-frame, `ArrayPool` no-garbage, first-frame/mid-stream watchdogs), the C920 USB-2 MJPG-30fps negotiation asserted from `EnumerateDescriptors()`, MJPEG decode as this stream's job (`Cv2.ImDecode`→BGR24, in-source `Cv2.Rotate`), and a `> [!IMPORTANT]` win-x64-only callout (FlashCap #182 native crash → macOS compile-only in CI). Verified: scope README-only; API refs fact-checked against ScanSettings (`FirstFrameTimeoutMs`=10s, `FrameWatchdogMs`=2s, `CameraRotationDegrees` default 90°) and the contract-pinned factory name.)*
- [x] **C4** 👤🧭 Hardware run on the Windows PC, gated on H6 (satisfied). The log shows 1080p MJPG at 30 fps. Memory stays flat over several minutes. A slow consumer causes latency, not growth. An unplug gives a clean error and a replug restarts. Decode time is recorded here. **Merge gate, then P4.**

  *Done 2026-09-22 with the user at the C920. Harness `e7cf54a` + fix `039d768`, docs `d313a51`, C3 merged from origin `e2b1305`: `Category=Hardware` tests in `Tests/StreamC/Hardware/`, the unplug test additionally `Interactive=Unplug`. Output goes to `%TEMP%\lorefetch-hw`, never the repo.*

  | Check | Measured |
  |---|---|
  | Negotiated | `HD Pro Webcam C920 1920x1080 MJPG @30fps (DirectShow)`, from the device's own characteristics. DShow 35 characteristics, MF 335; VfW "Default" enumerated and skipped |
  | Delivered | **28.6 fps** over 10 s; first frame ~720–790 ms after `StartAsync` |
  | **JPEG decode** | **11–15 ms mean** per 150-frame window, max 42 ms (one outlier). ~40% of one core at a full 30 fps; drop-before-decode means only consumed frames pay it |
  | Sustained, 3 min | 5,035 frames; private bytes oscillated 180–190 MB, post-warm-up growth **9.5 MB** (bound 100 MB) |
  | Slow consumer | 500 ms/frame for 60 s: **+5.7 MB**; capture→consume latency mean 33 ms, max 77 ms, not trending |
  | Unplug | `FrameSourceException` **2,002 ms** after the last frame (`FrameWatchdogMs` 2000); `DisposeAsync` 23 ms |
  | Replug | Reopen OK, first frame 719 ms |

  **Orchestrator chaos, the case the brief did not name:** deleting `StartAsync` first failed the live test for the **wrong reason**. The harness's 5 s per-`MoveNext` bound was shorter than `FirstFrameTimeoutMs` (10 s), and its `finally` disposed the async iterator mid-`MoveNext` → `NotSupportedException`, masking the product's diagnosis. Fixed in `039d768`: bounds are derived from the settings, and the harness cancels, then settles, then disposes. A non-hardware unit test pins both halves, and each was chaos-tested. Re-run on hardware, the chaos now fails with the product's own `FrameSourceException: No frame arrived within 10000 ms of opening the capture device…`. The four unattended tests pass on the fixed harness (4/4). The unplug test ran on the pre-fix harness; its fix touched only the bound, which was already 130 s.

  **Open, physical, not a C blocker:** rotation *direction*. The captured frame was shot before the mount (table depth ran across the frame), so whether 90° or 270° is upright is decided at H1 mount time. It is a `ScanSettings.CameraRotationDegrees` value, not code.
  **Merged and pushed 2026-09-22:** `main` `c26ae12` (full local suite green on Windows), `stream/c` `e2b1305`. CI [run 35757384775](https://github.com/jnapoli87/LoreFetch/actions/runs/35757384775): `windows-latest` ✅. `macos-latest` ❌ **at Test, with Restore and Build green**. The log needs a signed-in account and was not read. Suspected, unconfirmed: the first macOS-CI test to call OpenCV natively (C1b's `ImEncode`/`ImDecode`), i.e. Risk 7's `osx.arm64` runtime. **User ruling (2026-09-22): macOS passing is not a requirement. Do not spend effort on this leg.** Its Build step still proves `Core` portability. A red Test step there is expected until someone chooses to look.
  **Found in passing, outside C:** `AvaloniaUI.DiagnosticsSupport` 2.2.3 declares no licence, yet `App.csproj` references it unconditionally, so it would ship in the single-file exe. That contradicts RECONCILIATION's "nothing is redistributed". Spun off as a main-only task: make it Debug-only.

### Stream D — Collection & export · worktree `stream-d` · scope `src/LoreFetch.Core/Collection/**`, `src/LoreFetch.Core/Export/**`, `Tests/StreamD/**`, README §D
Global overrides for every D brief:
- v1 is **native + Moxfield**.
- `null` is the only representation of "unassessed".
- Writing uses a temp file in the **same directory**, then `Flush(true)`, then `File.Move(overwrite:true)`, then a `.bak` of the previous file.
- The native format has a BOM. Moxfield has none.
- Never sanitise `+2 Mace`.
- Duplicate rows merge on read, justified by import robustness.

- [x] **D1** Native CSV codec: — *done 2026-09-21, `stream/d` `f952d55`. Hand-written RFC 4180 codec (`NativeCsvCodec`, 539 lines — over the tripwire on the tokenizer and doc comments, not scope); CsvHelper rejected because no CSV library refuses unknown extra columns. Header checked as a column **set** (order-tolerant). Blank ↔ `null` for `Condition` and `ArtworkId`: quoted-empty and unquoted-empty are indistinguishable, so there is no third state. Duplicate merge sums quantity, keeps the newest row's name/distance/source (as the stub does), and folds `ArtworkId` agree-or-null via a public `FoldArtworkId` for D3 to reuse. 39 tests; all 4 briefed chaos cases fail correctly, and an independent one (whitespace-before-quote treated as a quote) fails `LeadingWhitespaceBeforeQuote_…`.*
  - an RFC 4180 writer that quotes on `,`, `"`, CR and LF, and whenever a field contains a quote
  - a parser that rejects whitespace before an opening quote as a quote, treating it as content
  - an exact header check, which throws on any unknown or missing column
  - blank ↔ `null` for Condition
  - invariant culture, with ISO-8601 round-trip (`"o"`) timestamps
  - duplicate merge with a log line

  Accept: all 5 vectors from stream-d §D3 round-trip; the BOM is not taken as data; an empty collection produces a header-only file.

  **Override — the header set now includes `ArtworkId`** (ruling 2026-09-21): appended last, `string?`, set only on `Source.Hash` rows. The exact-header check and the documented format version must include it. Its fold rule is **agree-or-null**: when merging duplicates, keep the id only if every merged row agrees, otherwise null.

  **Override, from S0.5b's finding — do not skip this, it is a bug that hides by construction:** the `Condition` half of the `OracleId` + `Condition` dedup key is **unreachable through `CommitCohortAsync`**, because `CohortTile` carries no condition and v1 never assesses one. It is reachable **only through the reader**, on a file that already has conditions in it. So the duplicate-merge tests must be driven from a **written file**, and must cover `null` vs `""` as distinct keys explicitly. Proven on the stub: with the merge key conflating them, 79 unrelated tests passed and nothing failed. CONTRACTS.md calls this the case that "would silently split one card into two rows"; a suite built only around commits will not see it.

  **The same trap applies to `ArtworkId`'s fold, for the same structural reason:** a commit-only suite cannot tell agree-or-null from last-write-wins. Drive that from a written file too, with rows that agree and rows that disagree.
- [x] **D2** `NativeCsvExporter`: the shared codec, `leaveOpen: true`, and a BOM. Accept: the caller's stream is still usable after export. — *done 2026-09-21, `stream/d` `1eb6390`. `IsVerified = true` for native, justified: its only target tool is LoreFetch's own reader, which the codec suite covers. leaveOpen chaos fails correctly.*
- [x] **D3** `CsvCollectionStore`: — *done 2026-09-21, `stream/d` `690ecce` + review fix `ceb2110`. **Manual rows:** the store derives null distance and null `ArtworkId` from the tile state rather than trusting the tile; a reflection-forced test proves it. **The plan's own lock case was too weak:** under `FileShare.None` the commit fails at its *read* step, before any temp file exists, so deleting the temp-file cleanup left every test green. A `FileShare.ReadWrite` lock (the "Excel has it open" case) reaches `File.Move` and fails there, and that test asserts the full directory listing; the `FileShare.None` case stays for the exception and the untouched original. 58 tests; chaos (cleanup removed, exception conversion removed) fails correctly.*
  - commits `Included` and `ManuallySet` tiles, folding duplicates within a cohort
  - returns the number of **cards** committed
  - throws on a null `Chosen`
  - uses the write sequence above
  - raises `CollectionStoreException` on a locked file
  - `ListAsync` on a missing file returns an empty list

  Accept: the blank-condition round trip yields 1 row with quantity 2; nine Forests return 9; a file locked with `FileShare.None` throws and leaves the original byte-identical; the temp file lives in the target directory.
- [x] **D4** `MoxfieldCsvExporter`: the header per stream-d §D2 (`Count`, `Name`, printing columns blank), no BOM, `IsVerified = false`. Accept: tests for the header and the 5 vectors. *(stream/d `68e749f`; also `ed60d6c` marks the write-lock test WindowsOnly — macOS `rename(2)` replaces an open target. 70 StreamD + 143 Integration green on Mac filter; chaos: CRLF flip fails 9 tests, line endings pinned.)*
- [x] **D5** 👤 Real import into Moxfield (gated on H4). Record the tool, date, row count, and what printings and blank conditions resolved to in README §D. Then set `IsVerified = true`. *(stream/d `bd106e0`; user imported a 6-card sample via Moxfield's Collection-view CSV upload on 2026-09-22 — all 6 names intact incl. `+2 Mace`/`Borrowing 100,000 Arrows`/`Kongming, "Sleeping Dragon"`/`Lim-Dûl's Vault`, `Forest` merged to qty 9, blank Condition → Near Mint, printings auto-assigned by Moxfield as expected for oracle-name-only rows. `IsVerified` flipped false→true; StreamD metadata test now asserts true, chaos-checked. README §D amended with the import instruction + verified result. StreamD 70 pass, Integration 143 pass/8 skip.)*
- [x] **D6** README §D: the unsupported-tools table and the `+2 Mace` Excel note. **Merge gate, then P5.** *(stream/d `844c7f3`; native SOT + Moxfield, unsupported table for ManaBox/Archidekt/Deckbox/Dragon Shield, `+2 Mace` `> [!NOTE]`, oracle-name-only limitation cross-referenced. Only README.md; anchor + links verified. D5 will amend §D with the verification record.)*

### Integration I (on `main`, after the relevant streams have merged)
- [ ] **I1** After D merges: implement `Real` in `Tests/Integration` for the store and exporters, and switch `AppComposition` to use them.
- [ ] **I2** After A and B merge: `Real` gets the detector, rectifier, identifier and catalog, loaded from `DataFiles`. B7's synthetic frames take the `FolderFrameSource` slot. `AppComposition` gets a `Real` mode that loads the index and thresholds.
- [ ] **I3** After C merges: `WebcamFrameSourceFactory` goes into `AppComposition`, with a switch between the demo folder and the camera.
- [ ] **I4** 🧭 Grep for any hardcoded distance outside `Core/Scanning`, `CohortTile` and `thresholds.json`. There must be none.
- [ ] **I5** `THIRD-PARTY-NOTICES`: an entry for every package, so the hook's advisory list is empty. Chase down the FFmpeg notices in the OpenCvSharp runtimes (PLAN risk 7).
- [ ] **I6** 🧭👤 `LOREFETCH_REQUIRE_REAL=1 dotnet test` on the **Windows PC** with every artifact present gives **0 skipped**. Then P6.

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

