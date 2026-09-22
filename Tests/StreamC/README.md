# LoreFetch.Tests.StreamC

Unit tests for `LoreFetch.Capture` — the FlashCap → `IFrameSource` adapter. Owned exclusively by **Stream C** (worktree `stream-c`) — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## What it covers

Level 1 (unit) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): format negotiation, rotation application, frame ownership/disposal. `LoreFetch.Capture` grants this project `InternalsVisibleTo` for its internal capture stage, so this project's assembly name must keep matching what `Capture.csproj` declares. Hardware-dependent cases (live device, sustained memory) are manual, on the Windows PC, per [`../../docs/TESTING.md`](../../docs/TESTING.md#hardware--manual) — not run here or in CI.

## Currently on `main`

`PlaceholderTests.cs` only — Stream 0's placeholder, replaced as stream C's own tests land.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/StreamC/LoreFetch.Tests.StreamC.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

Internals: documented by stream C at its done-when step.
