# Retrospective — what the build actually produced

> [!NOTE]
> The contract surface held: 10 interfaces, 558 lines, zero infrastructure leakage, every one backed by both a real implementation and a fake. That is what made four parallel streams possible, and it survived contact with all four. The two places complexity actually accumulated are `AppComposition.cs` (834 lines) and `MainWindow.axaml.cs` (829 lines) — both in the App layer, both fixable without touching the contracts.

Measured 2026-09-23 against `main` at `178b712`. Numbers here come from the tree, not from the planning docs.

## Shape of the thing

| | |
|---|---|
| Commits | 272 (92 on 2026-09-21, 179 on 2026-09-22, 1 on 2026-09-23) |
| Source | 18,671 lines across 4 projects |
| Tests | 23,347 lines, 668 test methods |
| Contracts | 10 interfaces, 558 lines, all in `Core/Abstractions` |

Test lines exceed source lines. That ratio is the single most atypical thing about the output.

## The first release build

`v0.1.0` was cut on 2026-09-22 and has required no product-code change since. The only two follow-up commits corrected tests rather than the application: a `FolderFrameSource` cycling test that asserted stricter frame delivery than `IFrameSource` guarantees, and a `ResolveCacheDir` test that hardcoded a path separator. Both are what a suite finds when it meets its second operating system, and neither reached the product.

The release notes are the other half of this result. They named five failure modes in advance, in user-facing terms: oracle name only with reprints indistinguishable, modern-frame English non-foil single-faced scope, sets newer than the index going unrecognised, edge-to-edge cards merging into one detection, and dark mats degrading detection. None of those were discovered after shipping.

So the first build holding is only half an achievement about the build. The other half is that the expectations it met had been written down accurately before anyone ran it, which is the part the approach should be credited for.

## Dependency direction

```
Core  ←  Capture  ←  App
  ↖ Lab (separate Exe — App does not reference it)
```

Acyclic, with `Core` as the leaf. `Lab` is 8,570 lines — 46% of all source — and ships in nothing: it is the measurement harness (index build, accuracy sweeps, crop-scale experiments, round-trip gate). Keeping it as its own `Exe` rather than a folder inside `Core` is why that 46% costs the shipped app nothing.

## Is it open for extension, closed for modification?

Yes, and not as an aspiration — every contract has two or more implementations already:

| Contract | Implementations |
|---|---|
| `ICollectionExporter` | Native CSV, Moxfield CSV, stub |
| `IFrameSource` | Webcam (FlashCap), folder, already-failed |
| `ICardIdentifier` | Hash, demo, stub |
| `ICardDetector` | Contour, layout-following fake, stub |
| `ICollectionStore` | CSV, stub |

Adding a third-party export format is a new class and one line at the composition root. Nothing existing gets edited. That is the actual test of OCP, and it passes — because the fakes forced a second implementation of everything *before* the real one existed, rather than the interface being drawn around a single concrete class after the fact.

## Are there too many abstractions?

No — if anything the set is minimal. Ten interfaces across 18.7k lines, each with multiple live implementations and each one load-bearing for either the stream fork or the test strategy. The diagnostic for ceremony is an interface with one implementation and no test double; there are none of those here.

## Is infrastructure separate from business logic?

At the contract level, cleanly. `Core/Abstractions` imports nothing but `System.Buffers` — no OpenCV, no CsvHelper, no Avalonia. `ScanPipeline` orchestrates five interfaces and knows about no concrete type. Frames cross into the UI as `byte[]`, so the UI layer never sees a `Mat`.

At the assembly level, less cleanly, and this is the known tradeoff: `Core` itself references `OpenCvSharp4` and `CsvHelper`, so `Core/Imaging` and `Core/Collection` sit in the same assembly as the domain. The seam exists at the interface boundary but is not enforced by the compiler — nothing stops a future domain type from taking a native dependency. Splitting `Core.Abstractions` into its own assembly would enforce it. For v1 scope that is ceremony; revisit it when a second consumer of the contracts appears.

## Where refactor time should go, ranked

1. **`src/LoreFetch.App/AppComposition.cs` (834 lines).** The composition root has absorbed jobs that are not composition: mode resolution from environment, thresholds-file loading, index loading, collection-path resolution, demo-frame folder selection, an `IsImageFile` helper, and two adapter classes declared inline (`RealWebcamFrameSourceFactory`, `AlreadyFailedFrameSource`). It also exposes two entry points, `CreateAsync` and `ComposeAsync`, which reads as accretion rather than design. Extract the two adapters to their own files, move environment and path resolution to a settings-resolution type, and split fakes-mode from real-mode wiring. This is the highest-value cleanup in the repo and it touches no frozen surface.

2. **`src/LoreFetch.App/MainWindow.axaml.cs` (829 lines) against `MainViewModel.cs` (298 lines).** MVVM is inverted — code-behind is 2.8× the view model, with `CommunityToolkit.Mvvm` already referenced. Some of that genuinely belongs in the view: the `WriteableBitmap` lifecycle, render scheduling, and quad-overlay drawing are view-layer resource management and moving them into a view model would be worse. What does not belong there is the type-ahead wiring (three handlers, roughly 80 lines), the tile context-menu handlers, and export invocation. Those are state and intent, not rendering.

3. **Doc/code drift in `src/LoreFetch.App/README.md`.** It states the App project "does not reference OpenCvSharp." True of the `.csproj` — the dependency is transitive through `Core` — but `DemoFrames.cs:1` is `using OpenCvSharp;`. The claim is defensible as written and wrong as read. Fix the sentence, not the code; `DemoFrames` uses OpenCV deliberately, to avoid shipping any image asset under the imagery ban.

4. **`Lab` experiment pruning.** Not a defect today. But `CropScale`, `ExpandExperimentCommand` (542 lines) and `MultiScaleSweep` are experiments whose conclusions are now baked into the committed thresholds file. Once the accuracy numbers are settled, most of that is answered-question code. It ships nowhere, so it is low-risk to leave and low-cost to delete — decide deliberately rather than by neglect.

## What to dig into, in the order that teaches most

The implementation complexity worth debugging through, roughly hardest-to-fake understanding first:

1. **`Core/Imaging/QueryTransform.cs` and the reference-side transform, read side by side.** The asymmetry between them is the single most load-bearing design decision in the project and the easiest to break silently. Run the round-trip gate, then deliberately change one side and watch the distance floor move.
2. **`Core/Scanning/ScanPipeline.cs`, specifically the retained-frame handoff.** Its own header comment says its bugs are lifetime and threading bugs rather than logic bugs. Two locks, a frame that the loop thread disposes while another thread may claim ownership. This is where a real concurrency bug would live.
3. **`Core/Imaging/ContourCardDetector.cs` (517 lines).** The rejection-reason logging is the instrumentation to read first — feed it real frames and watch what it discards and why.
4. **`Core/Identification/HashCardIdentifier.cs` + `HashIndexFile.cs`.** Brute-force Hamming over the index. The measured 0.243 ms per query is the claim to verify yourself.
5. **`Core/Scanning/DualHypothesisIdentification.cs`.** Landed late; understand why a second hypothesis was needed before trusting it.

## What the orchestration approach did and did not establish

Established, with artifacts: contract-first design that survived four independent implementers; guardrails that were tested adversarially rather than assumed (three real defects found in the hooks, including the cwd hole that would have left the frozen surface fully writable); rejection decisions recorded with evidence rather than preference.

Not established: hands-on fluency in the CV and hashing layer. Understanding was gained by reading the complexity as it appeared, which is not the same as having debugged it. Items 1–5 above are the route to closing that, and the reason they are worth doing in that order is that each one is a place where the system fails *quietly* rather than loudly.
