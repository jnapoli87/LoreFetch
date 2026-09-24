# LoreFetch.Tests.Detection

Unit and real-capture tests for card detection and rectification: `ContourCardDetector` (aspect and area filtering, discard reasons, corner ordering, dedupe, the retrieval mode), `PerspectiveRectifier` (warp to the canonical 488×680 card, pinned `INTER_LINEAR`) and `QuadExpansion`. The Detection domain in the [domain map](../../docs/CONTRACTS.md#domain-map); design record in [`../../docs/design/identification.md`](../../docs/design/identification.md) (§B5).

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/Detection/LoreFetch.Tests.Detection.csproj -c Release
```

## Gated tests

Tests that need the Scryfall cache or real captures skip by default when those are absent, and fail instead under `LOREFETCH_REQUIRE_REAL=1`. See [`../Support/README.md`](../Support/README.md).
