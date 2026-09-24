# LoreFetch.Tests.StreamD

Unit tests for `Core/Collection` and `Core/Export` — the CSV collection store and export adapters (native and Moxfield). Owned exclusively by **Stream D** (worktree `stream-d`) — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#domain-map).

## What it covers

Level 1 (unit) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): dedup identity (`OracleId` + `Condition`, blank condition as a value), the atomic temp-then-rename write sequence, `ArtworkId`'s agree-or-null fold rule, malformed-line reporting, and per-format BOM handling on export. See [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#collection-and-export) for the write-sequence and versioning rules this exercises.

## Currently on `main`

`PlaceholderTests.cs` only — Stream 0's placeholder, replaced as stream D's own tests land.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/StreamD/LoreFetch.Tests.StreamD.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

Internals: documented by stream D at its done-when step.
