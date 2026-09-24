# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

LoreFetch turns a webcam into an offline Magic: The Gathering collection scanner. It detects cards in a frame, identifies each one by a 1024-bit perceptual hash against a committed index of Scryfall artwork, and commits the cards you accept to a CSV collection. Personal project, GPLv3. It ships `win-x64` only, and the code stays portable (a macOS CI leg enforces that). v0.1.0 was a 24-hour hackathon build by parallel agents; its record is in `docs/history/`, which is history, not instructions.

## Commands

Build, test and run through **`scripts/lorefetch.sh`** (`doctor`, `setup`, `build`, `test`, `run`; `--help` for options). It applies the same test filter CI does and fails when zero tests ran, which a bare `dotnet test` reports as success. The `lorefetch-run` skill covers calling it from a tool.

- **One project or one test:** `dotnet test Tests/Detection/LoreFetch.Tests.Detection.csproj -c Release --filter "Category!=Hardware&FullyQualifiedName~ContourCardDetectorTests"`.
- **Real-data tests** skip when the Scryfall cache (`LOREFETCH_SCRYFALL_CACHE`) or `test-images/` is missing. `LOREFETCH_REQUIRE_REAL=1` turns those skips into failures, which proves they ran. `Tests/Support/README.md` explains the gates.
- **App modes:** `LOREFETCH_MODE=fakes` runs on generated frames and stubs, with no camera and no index; unset means the real pipeline. `LOREFETCH_FRAMES_DIR` replays a folder of frames through the real pipeline without a camera, and `LOREFETCH_COLLECTION` sets the CSV path.
- **Maintainer tooling:** `dotnet run --project src/LoreFetch.Lab -- <command>` (`bulk`, `images`, `build-index`, `round-trip-gate`, `accuracy`, …).
- **CI metrics locally:** `scripts/lorefetch.sh test --results <dir>`, then `dotnet run scripts/Metrics.cs -- collect <dir> metrics.json`.

## Architecture

Four projects: **Core** (domain logic and the contract surface; portable), **Capture** (FlashCap webcam → `IFrameSource`), **App** (Avalonia UI; `AppComposition` is the composition root and the only place that knows concrete implementations), **Lab** (maintainer console: Scryfall pull, index build, accuracy; never shipped).

The scan loop, all behind interfaces in `Core/Abstractions`: `IFrameSource` → `ICardDetector` → `IRectifier` → `ICardIdentifier`, composed by `Core/Scanning/ScanPipeline`. A capture (Space, or the auto trigger in `Core/Trigger`) turns the latest frame's quads into a `Cohort` of tiles; Enter commits the cohort to `ICollectionStore`, a CSV file (`Core/Collection`). Frames cross the seam as pooled `byte[]` `CameraFrame`s that the consumer disposes, never as OpenCV `Mat`s.

The hash has two sides that share steps 4–6. The reference side (`ReferenceTransform`, run by the Lab's `build-index`) produced `data/index/cards.lfidx`. The query side (`QueryTransform`) runs on every capture. Both live in `Core/Identification`, each exactly once. `data/index/` is copied next to the app and found via `Core/Scanning/DataFiles`.

`Core/Fakes` stand in for every domain. They power demo mode and the fakes leg of the end-to-end suite in `Tests/Integration`, which runs over both fakes and real implementations. The domain map (code → test project) is in `docs/CONTRACTS.md`, and `Tests/Architecture` enforces the dependency directions between domains.

## Hard rules

- **Card imagery stays out of git**, including Scryfall renders and our own photos: the artwork is Wizards of the Coast IP. Commit only derived data (the index, accuracy tables). Local fixtures live in gitignored `test-images/`, and `hooks/pre-commit` rejects staged rasters.
- **The hash transforms change only together with an index rebuild on `win-x64`** and regenerated golden hashes. The goldens are `WindowsOnly` because `INTER_AREA` is not bit-exact on ARM64. Read `docs/DECISIONS.md` "The one gate that matters most" first.
- **Identification ranks every candidate by full Hamming distance and returns the top N**, with no threshold filtering or early rejection. Match distance drives UI emphasis; Enter is the gate.
- **Pin every OpenCV interpolation flag and border mode explicitly** (`docs/TESTING.md`).
- **Storage is CSV**: UTF-8 with BOM, written to a temp file in the same directory and then renamed, with a `.bak` kept. A header with unknown or missing columns fails loudly.
- **Scryfall requests carry our `User-Agent` and an `Accept` header, and honour 429s.** Pull `normal` images, never `small`.
- **Nothing reaches GitHub without the owner approving the exact content first**: pushes, PRs, issues, labels, releases, repo settings. Local commits on a branch are fine.
- **On a fresh clone, run `scripts/lorefetch.sh setup` before the first commit.** It wires the repo-local noreply identity and the pre-commit hook; `doctor` checks them.

**Read `docs/DECISIONS.md` before changing** identification, detection, camera geometry, the capture/commit keys, storage or export formats, the package stack (Avalonia, OpenCvSharp, FlashCap pins and traps), Scryfall access, or platform scope. It holds each settled decision, why it holds, and why the alternatives (OCR, embeddings, a database, …) stay rejected. Code comments cite its sections as `DECISIONS.md "<section>"`.

Other references: `CONTEXT.md` is the glossary (use its terms). `docs/design/*.md` are the per-domain design records whose section IDs (A0, B5, C2, D1…) code comments cite. `docs/TESTING.md` covers the test levels and CI checks.

## Working on LoreFetch

The owner is learning this codebase and works on it at a slow pace. Favour small, explained changes over sweeping ones, and put the *why* in commit messages. The reasoning is the part worth keeping.

**Follow [`CONTRIBUTING.md`](CONTRIBUTING.md)** for every change: issue → branch → PR → green CI → squash-merge, with terse issues and PRs. On top of it:

- **File the issue before opening the branch or PR**, so the branch name and `Fixes #<n>` both carry the issue's number (a PR opened first takes that number itself).
- **Draft issue and PR text for the owner's approval before posting it.** Terse: symptom and evidence for an issue; `Fixes #<n>`, the change and its verification for a PR. Decisions that come out of an issue land in the repo (`docs/DECISIONS.md`, `docs/CONTRACTS.md`, a design doc), and the issue links to them.
- **Chaos-test for real:** the planted bug must make the new test fail on the assertion that guards it. A plant that still passes proves nothing.
- **A failing `Tests/Architecture` rule** means either the change is wrong or a decision is changing. In the second case, update the rule and the doc it cites together.
- **The README changes in the same PR as the behaviour it describes.**
- **Plans are scratch.** Plan-mode files stay local. Anything in a plan worth keeping becomes an issue, a commit message, or a line in the docs.
