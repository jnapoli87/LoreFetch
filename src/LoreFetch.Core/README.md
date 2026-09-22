# LoreFetch.Core

The shared domain layer: the frozen contract surface, the scan pipeline that composes it, the seven fakes, and every stream's domain logic that isn't UI, capture I/O or a maintainer tool. No single stream owns this project as a whole — ownership is per subfolder, below.

## Dependency rules

Core has **no Avalonia/UI dependency and no FlashCap dependency**, and must stay portable — the `macos-latest` CI leg exists specifically to enforce that (see [`../../docs/TESTING.md`](../../docs/TESTING.md#ci)). It does reference OpenCvSharp4 (`Core/Imaging` and `FolderFrameSource` need it — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md)) and `CsvHelper` (insurance for `Core/Collection`), plus the logging abstractions seam. No contract type in `Abstractions` or `Scanning` exposes an OpenCvSharp type.

## Folder map

| Folder | Contents | Owner |
|---|---|---|
| `Abstractions/` | Frozen contract types and interfaces (frames, detection, identification, cohorts, settings) | Stream 0, frozen |
| `Scanning/` | `IScanPipeline` and the pipeline that composes source → detector → rectifier → identifier → trigger | Stream 0, frozen |
| `Fakes/` | The seven stub/fake implementations that unblock stream A | Stream 0, frozen |
| `Identification/` | Hash port and index lookup (not yet on `main`) | Stream B |
| `Imaging/` | Card detection and rectification (not yet on `main`) | Stream B |
| `Collection/` | CSV collection store (not yet on `main`) | Stream D |
| `Export/` | Export adapters (not yet on `main`) | Stream D |
| `Trigger/` | `IAutoCaptureTrigger` implementation (not yet on `main`) | Stream A |

## The freeze

`Abstractions/`, `Scanning/` and `Fakes/` are the frozen contract surface — see [`../../CLAUDE.md`](../../CLAUDE.md) and [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries). A stream that needs a change there stops and asks; it does not edit unilaterally. `.claude/hooks/guard-write.sh` enforces this from inside a linked worktree.

Internals: documented by each owning stream at its done-when step.
