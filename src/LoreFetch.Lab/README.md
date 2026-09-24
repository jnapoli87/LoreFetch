# LoreFetch.Lab

A maintainer console tool (`OutputType=Exe`), not shipped in the app package: builds the hash index from Scryfall bulk data and measures identification accuracy against the local fixture corpus. Its own domain in the [domain map](../../docs/CONTRACTS.md#domain-map).

## Dependencies

References `LoreFetch.Core` and OpenCvSharp4 directly (JPEG decode of Scryfall renders, the hashing pipeline). No other project references `Lab`, except `Tests/Integration`, which does so only to exercise the real implementation set once it exists.

## Commands

Index build, accuracy report and the diagnostic experiments; `dotnet run --project src/LoreFetch.Lab -- --help` lists them. The CLI's design is in [`../../docs/design/identification.md`](../../docs/design/identification.md).

## Data handling

The Scryfall bulk-data cache and downloaded renders live **outside this repository** — nothing under `src/LoreFetch.Lab` fetches into a tracked path. Only derived output is ever committed: the hash index and the accuracy tables (see [`../../CLAUDE.md`](../../CLAUDE.md)). **Card imagery is never committed**, by anyone, for any reason — enforced by `.gitignore` and `hooks/pre-commit`.

## Testing

Unit- and synthetic-integration-tested in `../../Tests/Lab`. Accuracy runs are local-only, reported as a committed table rather than pass/fail — see [`../../docs/TESTING.md`](../../docs/TESTING.md#the-five-levels).

## Diagnostic-only code

`CropScale/` (`CropScaleTransform`, `CropScaleExperimentRunner`, `MultiScaleSweepExperiment`, plus the `lab crop-scale` command) lives here, not in `Core/Imaging`, because it is diagnostic tooling for package B5c's crop-scale experiment — never part of the shipping reference or query path. B5c's 3-scale identifier-side sweep was **rejected for v1** (`docs/history/orchestration-plan.md`, "Rulings — the 90% gate and B5c's sweep": every real wrong match sits at 272–344, outside the ~5% crop tolerance the curve identified, so crop error is not what is failing real cards), so this code must not ship in the product assembly — `LoreFetch.App` never references `Lab`. Kept working and tested (`lab crop-scale`) because the curve is real evidence and stays reproducible; see `docs/accuracy.md`'s B5c section for the measurements it produced.

## Internals

Hand-rolled dispatch in `Program.cs` (the `.csproj` is frozen and can't take a CLI-parsing package) — one `switch` arm per command, each delegating to its own `*Command.RunAsync`:

| Command | Job |
|---|---|
| `bulk` | Download `unique_artwork`, run the filter cascade (English, has `image_uris.normal`, non-token/art-series layout, non-digital-only), write the surviving arts to a manifest |
| `printings` | Report the fraction of in-scope artworks with exactly one in-scope printing |
| `images` | Download the manifest's `image_uris.normal` renders into an external, gitignored cache, keyed by `ArtworkId`; resumable |
| `build-index` | Hash every cached render (`ReferenceTransform` → `CardHasher`) into a `cards.lfidx` |
| `detect` | Run `ContourCardDetector` against a still image and write an annotated PNG (accepted quads, rejected contours with discard reasons) |
| `identify` | Run detect → rectify → `Identify` against a real index and print the top candidates |
| `synth` | Build camera-like synthetic frames (card or bare-mat) for CI and local testing |
| `round-trip-gate` | Package B2: sample the committed index, retrieve each render through the query path, report the rank-1 `ArtworkId` match rate, reference floor and margins; `--out` writes them into a thresholds file |
| `query-hash-witness` | Write a fixed sample of query-side hashes for cross-architecture comparison |
| `crop-scale` | Package B5c: sweep a synthetic crop-scale error and report rank-1 rate vs. distance/margin, per level |
| `retrieval-experiment` | H1/H2/H3 real-frame detection investigation (retrieval mode × crop sweep) — see `docs/accuracy.md` |
| `accuracy` | Package B6: run the accuracy harness against whatever `test-images/ground-truth.csv` + `test-images/fixtures/` exists on disk; report correct@1/wrong@1/no-match, margins and the lands-excluded count |

Run `dotnet run --project src/LoreFetch.Lab -- <command>` with no arguments, or see `Program.cs`'s `PrintUsage()`, for the full flag list per command.

## Test gating

Most of `Tests/Lab`'s tests need real Scryfall renders on disk and are gated on an environment variable rather than a mock: `LOREFETCH_SCRYFALL_CACHE` (default `~/LoreFetchData/scryfall-cache`; on the Windows PC that's `C:\LoreFetchData\scryfall-cache`). If the variable isn't set, or the directory it names doesn't exist, those tests **skip silently** rather than fail — a machine whose cache lives somewhere else just runs a smaller suite, with no red to flag it. Set the variable explicitly rather than assuming the default matches your machine.

`LOREFETCH_REQUIRE_REAL=1` flips that default: with it set, a cache-gated test **fails** instead of skipping when the cache is missing, so CI (or a deliberate local run) can assert the real-data tests actually ran rather than silently passed by skipping all of them.

Real-capture regression tests additionally need `test-images/` (gitignored, never committed — see [`../../CLAUDE.md`](../../CLAUDE.md#card-imagery-is-enforced-in-two-layers-not-just-documented)): `test-images/ad-hoc/` for individual detector fixtures, `test-images/fixtures/<height>in/<layout>/` plus `test-images/ground-truth.csv` for the accuracy harness. Those also skip, not fail, when absent.

Golden hashes (`Tests/Identification`'s committed 1024-bit expected values) carry `[Trait("Category","WindowsOnly")]` and are filtered out on the `macos-latest` CI leg, because `INTER_AREA` is not bit-exact across x86-64 and ARM64 — see [`../../CLAUDE.md`](../../CLAUDE.md#the-one-gate-that-matters-most). The committed index and the goldens are both generated on `win-x64` only.
