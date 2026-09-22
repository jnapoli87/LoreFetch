# LoreFetch.Tests.Integration

The end-to-end suite: `IFrameSource` → detect → rectify → identify → cohort → commit → CSV, parameterised over an implementation set (`[fakes]` from the end of Stream 0, `[real]` once each artifact exists). Also holds Stream 0's own unit tests (`Unit/`) — `CohortTile`, `ScanPipeline`, `ThresholdsFile` and the fakes' own behaviour — since Stream 0 owns exactly one test project. Owned by **Stream 0**: frozen, not edited by the streams (see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries) and [`../../docs/orchestration-plan.md`](../../docs/orchestration-plan.md), V18).

## What it covers

Level 2 (integration, fakes) and level 3 (integration, real — same tests, real implementations injected) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels). References every `src/` project, including `App`, so it can exercise the real implementation set once streams merge.

## Real-implementation skips

Real-implementation cases skip until their artifact exists (hash index, Scryfall cache, etc.), via `EndToEnd/RealArtifactGate.cs`. That is honest signal, not failure — see [`../../docs/TESTING.md`](../../docs/TESTING.md#2-real-implementations-reuse-the-same-tests-gated-by-skip). Set `LOREFETCH_REQUIRE_REAL=1` to turn every artifact-gated skip into a failure — this is the mechanical definition of "integration done" (V7), meant for a local `win-x64` run with every artifact present, not for CI.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/Integration/LoreFetch.Tests.Integration.csproj -c Release
```

`dotnet test` **exits 0 even when a project discovers zero tests** — read the `Passed`/`Total` counts in the summary, don't trust the exit code alone. `scripts/lorefetch.sh test` checks this for you.

Internals: documented by Stream 0 at its done-when step.
