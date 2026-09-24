# LoreFetch.Tests.Lab

Unit and synthetic-integration tests for `LoreFetch.Lab`, the maintainer tooling: Scryfall bulk filtering and image download, the index build, synthetic frame generation, the round-trip gate and the accuracy harness, plus the crop-scale experiments. The Lab domain in the [domain map](../../docs/CONTRACTS.md#domain-map). Detector and hash tests live in [`../Detection`](../Detection/) and [`../Identification`](../Identification/).

## What it covers

Level 1 (unit) and level 3 (synthetic integration) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): the round-trip gate — a Scryfall render must retrieve its own artwork (`ArtworkId`, not just `OracleId`) through the full query path, asserting a recorded distance bound rather than ≈ 0. Accuracy measurement itself is a local-only committed table, not a pass/fail test here.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/Lab/LoreFetch.Tests.Lab.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

## Gated tests

Tests that need the Scryfall cache or real captures skip by default when those are absent, and fail instead under `LOREFETCH_REQUIRE_REAL=1`. How the cache directory is resolved, and what each gate needs, is in [`../Support/README.md`](../Support/README.md).
