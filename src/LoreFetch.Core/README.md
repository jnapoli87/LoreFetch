# LoreFetch.Core

The shared domain layer: the contract surface, the scan pipeline that composes it, the seven fakes, and every domain's logic that isn't UI, capture I/O or a maintainer tool. It holds several domains, one per subfolder, below.

## Dependency rules

Core has **no Avalonia/UI dependency and no FlashCap dependency**, and must stay portable — the `macos-latest` CI leg exists specifically to enforce that (see [`../../docs/TESTING.md`](../../docs/TESTING.md#ci)). It does reference OpenCvSharp4 (`Core/Detection`, `Core/Identification` and `FolderFrameSource` need it — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md)) and `CsvHelper` (insurance for `Core/Collection`), plus the logging abstractions seam. No contract type in `Abstractions` or `Scanning` exposes an OpenCvSharp type.

## Folder map

| Folder | Contents | Domain |
|---|---|---|
| `Abstractions/` | Contract types and interfaces (frames, detection, identification, cohorts, settings) | Contract surface |
| `Scanning/` | `IScanPipeline` and the pipeline that composes source → detector → rectifier → identifier → trigger | Contract surface |
| `Fakes/` | The seven stub/fake implementations: demo mode and the fakes leg of the end-to-end suite | Contract surface |
| `Detection/` | Card detection and rectification | Detection |
| `Identification/` | Hash port (reference and query transforms, hasher) and index lookup | Identification |
| `Collection/` | CSV collection store | Collection |
| `Export/` | Export adapters | Collection |
| `Trigger/` | `IAutoCaptureTrigger` implementation | App |

## The contract surface

`Abstractions/`, `Scanning/` and `Fakes/` are the contract surface: every domain builds against them, so a change there affects all of them at once. Make such changes deliberately and call them out in the PR; see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#domain-map).

## Internals

**Detection — `Detection/`.** `ContourCardDetector` finds card-shaped contours per frame (aspect + area filtered, discard reasons logged) and `PerspectiveRectifier` warps the detected quad to the canonical 488×680 `RectifiedCard`, pinned `INTER_LINEAR` (`warpPerspective` doesn't support `INTER_AREA`). `FrameMat` converts a `CameraFrame` to an OpenCV `Mat`.

**Identification — `Identification/`.** `ReferenceTransform` and `QueryTransform` hold the reference-only and shared hash steps respectively — each exists exactly once — and `CardHasher` turns a prepared image into the 1024-bit `CardHash`. `HashIndexFile` reads and writes the committed `.lfidx` index, and `HashCardIdentifier` implements `ICardIdentifier` over a loaded index, matching by full Hamming distance with no threshold filtering. Nothing here depends on `Detection/`: identification starts from an already-rectified card.

See [README: How it works](../../README.md#how-it-works) and [`docs/design/identification.md`](../../docs/design/identification.md) for the numbers behind both.
