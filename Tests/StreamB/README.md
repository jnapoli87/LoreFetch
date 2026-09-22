# LoreFetch.Tests.StreamB

Unit and synthetic-integration tests for `Core/Identification`, `Core/Imaging` and `LoreFetch.Lab` — the hash port, card detection/rectification, and index-build tooling. Owned exclusively by **Stream B** (worktree `stream-b`) — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## What it covers

Level 1 (unit) and level 3 (synthetic integration) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): hash invariants (bounds and golden byte values, not scale/brightness equality), quad-filtering, and the round-trip gate — a Scryfall render must retrieve its own artwork (`ArtworkId`, not just `OracleId`) through the full query path, asserting a recorded distance bound rather than ≈ 0. Accuracy measurement itself is a local-only committed table, not a pass/fail test here.

## Golden hashes are Windows-only

Committed golden hashes carry `[Trait("Category","WindowsOnly")]` and are filtered out on the `macos-latest` CI leg, because `INTER_AREA` is not bit-exact across x86-64 and ARM64 — see [`../../CLAUDE.md`](../../CLAUDE.md#the-one-gate-that-matters-most). The index is built and the goldens generated on `win-x64` only.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/StreamB/LoreFetch.Tests.StreamB.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

Internals: documented by stream B at its done-when step.
