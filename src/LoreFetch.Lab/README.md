# LoreFetch.Lab

A maintainer console tool (`OutputType=Exe`), not shipped in the app package: builds the hash index from Scryfall bulk data and measures identification accuracy against the local fixture corpus. Owned by **Stream B** (worktree `stream-b`), alongside `Core/Identification/**`, `Core/Imaging/**` and `Tests/StreamB/**` — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## Dependencies

References `LoreFetch.Core` and OpenCvSharp4 directly (JPEG decode of Scryfall renders, the hashing pipeline). No other project references `Lab`, except `Tests/Integration`, which does so only to exercise the real implementation set once it exists.

## Commands

Commands (index build, accuracy report) are being added by stream B; only `Program.cs` exists on `main` today. See [`../../docs/stream-b-identification.md`](../../docs/stream-b-identification.md) for the planned CLI shape.

## Data handling

The Scryfall bulk-data cache and downloaded renders live **outside this repository** — nothing under `src/LoreFetch.Lab` fetches into a tracked path. Only derived output is ever committed: the hash index and the accuracy tables (see [`../../CLAUDE.md`](../../CLAUDE.md)). **Card imagery is never committed**, by anyone, for any reason — enforced by `.gitignore` and `hooks/pre-commit`.

## Testing

Unit- and synthetic-integration-tested in `../../Tests/StreamB`. Accuracy runs are local-only, reported as a committed table rather than pass/fail — see [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels).

Internals: documented by stream B at its done-when step.
