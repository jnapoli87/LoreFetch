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

## Internals

Most tests here are ordinary unit tests and run everywhere, with no gate. Three kinds of test are gated instead, each skipping (not failing) by default when what it needs isn't on the machine:

- **Cache-gated tests** (round-trip gate, query-hash witness, accuracy against the real corpus, and anything else that hashes a real Scryfall render) resolve their cache directory via `RoundTripGateTests.ResolveCacheDir()`, in order: (1) `$LOREFETCH_SCRYFALL_CACHE`, when set and non-blank; (2) `~/LoreFetchData/scryfall-cache`, if that directory already exists; (3) on Windows only, `C:\LoreFetchData\scryfall-cache`, if that directory exists — the plan's documented Windows location, found without setting the env var at all; (4) otherwise the home path from step 2, even though it doesn't exist, so a skip message still names a sensible place. Pointing at a directory that doesn't exist (or resolving to one via step 4) makes these tests skip silently rather than fail: a machine whose cache lives somewhere else entirely just runs a smaller suite with no red flag. Set the variable explicitly before trusting a "0 failed" run, or check the skip reason for the path it actually tried.
- **Real-capture tests** additionally need `test-images/` on disk (gitignored, never committed — card imagery, per CLAUDE.md): `test-images/ad-hoc/` for individual detector fixtures, `test-images/fixtures/<height>in/<layout>/` plus `test-images/ground-truth.csv` for the accuracy harness against the real 54-slot corpus. Also skips, not fails, when absent.
- **`LOREFETCH_REQUIRE_REAL=1`** flips both of the above from skip to fail (`RealCaptureGate.SkipOrFail`) — set it to prove, on a machine that does have the cache and fixtures, that the real-data tests actually ran rather than silently passed by skipping.

The `WindowsOnly`-traited goldens (above) are filtered for a different reason than these three — an architecture mismatch rather than missing local data — so they stay filtered even on a Windows machine that has never run `LOREFETCH_REQUIRE_REAL`.
