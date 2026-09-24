# LoreFetch.Tests.Identification

Unit tests for the hash port and index lookup: `CardHash`, `CardHasher` (region, order statistic), the reference and query transforms, the golden hashes, `HashIndexFile`, `HashCardIdentifier` (ranking by full Hamming distance, no threshold filtering, plus a soft performance bound) and the committed thresholds file. The Identification domain in the [domain map](../../docs/CONTRACTS.md#domain-map); design record in [`../../docs/design/identification.md`](../../docs/design/identification.md).

## Golden hashes are Windows-only

Committed golden hashes carry `[Trait("Category","WindowsOnly")]` and are filtered out on the `macos-latest` CI leg, because `INTER_AREA` is not bit-exact across x86-64 and ARM64 — see [`../../CLAUDE.md`](../../CLAUDE.md#the-one-gate-that-matters-most). The index is built and the goldens generated on `win-x64` only.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/Identification/LoreFetch.Tests.Identification.csproj -c Release
```

## Gated tests

Tests that need the Scryfall cache or real captures skip by default when those are absent, and fail instead under `LOREFETCH_REQUIRE_REAL=1`. See [`../Support/README.md`](../Support/README.md).
