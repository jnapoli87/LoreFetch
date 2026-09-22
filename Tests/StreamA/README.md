# LoreFetch.Tests.StreamA

Unit tests for `LoreFetch.App` and `Core/Trigger` — view-model logic, tile-state interaction and the auto-capture trigger's state machine. Owned exclusively by **Stream A** (worktree `stream-a`) — see [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#stream-boundaries).

## What it covers

Level 1 (unit) of [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels): `IAutoCaptureTrigger` is called out there as the highest-value unit target in the project (fires once on settle, re-arm rule, no re-fire on a static tableau). Uses `Avalonia.Headless.XUnit` for keyboard-map and control tests — the only automated path onto Avalonia UI logic. Full visual rendering is verified manually, not here.

## Currently on `main`

`PlaceholderTests.cs` only — Stream 0's placeholder, replaced as stream A's own tests land.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/StreamA/LoreFetch.Tests.StreamA.csproj -c Release
```

`dotnet test` exits 0 even when a project discovers zero tests — check the `Passed`/`Total` counts, not just the exit code.

Internals: documented by stream A at its done-when step.
