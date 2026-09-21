# Stream B — Identification

**The engine, and the only stream that can invalidate the project.** Everything else is plumbing around whatever this stream proves.

Owns (exclusive write access): `LoreFetch.Core/Identification/**`, `LoreFetch.Core/Imaging/**`, `LoreFetch.Lab/**`, `Tests/StreamB/**`, the stream B section of `README.md`, and the committed hash index + thresholds file
Consumes: `Core/Abstractions` (frozen — see [`CONTRACTS.md`](CONTRACTS.md)), the local fixture corpus
Implements: `ICardDetector`, `IRectifier`, `ICardIdentifier`, `IOracleCatalog`
Must not touch: `LoreFetch.App`, `LoreFetch.Capture`, `Core/Trigger`, `Core/Collection`, `Core/Export`, `Core/Scanning`, the fakes, any `.csproj`, `LoreFetch.slnx`

Needs no camera and no UI. Runs entirely on images on disk, on the Mac.

---

## What it implements

A C# port of **CardSpotter**'s 1024-bit perceptual hash (`github.com/relgin/cardspotter`, **BSD-3-Clause**, GPLv3-compatible, must be attributed in `THIRD-PARTY-NOTICES`). This is the algorithm Wizards' SpellTable actually ships — verified by inspecting its production WASM bundle, which contains no neural net.

Seven steps. **The reference side and the query side must apply byte-identical transforms.**

1. Rectify the card (perspective-correct from the detected quad)
2. `GaussianBlur` 3×3, σ=1
3. Resize to **96 px wide**, `INTER_AREA`, grayscale
4. Take region `width × 0.85·width` — the **top ~61%**: title, art *and* type line
5. Resize that region to **32×32**
6. 4×4 grid of 8×8 cells; each bit = pixel > **median of its own cell** → **1024 bits**
7. Match by Hamming distance, with per-cell grid distances and early rejection

Why it survives webcam frames where naive pHash fails: rectification removes distortion rather than tolerating it; blurring and downsampling the *reference* side destroys the high-frequency detail a webcam can't reproduce, so photo and render converge; the **local median per cell** makes each bit invariant to local brightness, exposure and white balance; the 4×4 grid lets glare corrupt some cells instead of failing the whole match.

---

## Tasks, in order

### B1 — Port the hash
Implement per the seven steps. Unit-test the invariants rather than golden values:
- the same image at two input scales produces the same hash
- a global brightness or gamma shift leaves the local-median bits stable
- an inverted image does *not* match

### B2 — The round-trip gate ⚠️ do this before anything builds on the hash
**A Scryfall render must retrieve *itself* at Hamming distance ≈ 0 through the full query path.** Not through a test-only shortcut — through the same code the scanner will call.

If reference-side and query-side transforms diverge at all — a different interpolation, a different region offset, a stray colour conversion — **matching degrades silently instead of failing loudly.** That is the single highest-risk failure mode in the project, and it is cheap to detect here and expensive to detect at integration.

The structural defence: **the index builder and the scanner share one transform implementation.** Not two implementations that agree; one function, called twice.

### B3 — `HashIndex`
Hamming matching with per-cell grid distances and early rejection. ~67k × 1024-bit brute force is microseconds, so no ANN structure is needed — don't add one. Returns a **ranked** candidate list; the accuracy harness needs rank-N to compute distance margins.

`ICardIdentifier.Identify` **never filters by threshold** — it returns the nearest `maxCandidates`, always. The scan pipeline applies `GoodDistance`/`OkDistance` in one place. (Early rejection inside the search is fine: it prunes cards that can't make the top N, not cards beyond a threshold.)

`IOracleCatalog` is implemented from the same index: every distinct `(OracleId, OracleName)` it carries.

### B4 — Index builder (maintainer tool, in `Lab`)
From Scryfall bulk data:
- Resolve via `GET https://api.scryfall.com/bulk-data/unique_artwork`. Bulk is **gzipped JSONL** — field `jsonl_download_uri`, size field `compressed_size`. The old `download_uri`/`size` fields are gone; most tutorials are stale. Filenames carry a daily timestamp, so never hardcode.
- Pull `small` images (~0.74 GB total, ~1 hour). `*.scryfall.io` is **unmetered**; only `api.scryfall.com` is rate-limited (2/sec on `/cards/*`).
- `User-Agent: LoreFetch/0.1 (github.com/jnapoli87/LoreFetch)` **and** `Accept` are both mandatory.
- Filter on `image_status` — skip `placeholder` and `missing`.
- Output: compact binary, **~8.6 MB** (1024 bits × ~67k) plus the oracle-name mapping. Committed to the repo; it's derived fingerprints, no imagery.
- **Never commit the downloaded images.** Cache them outside the repo or in a gitignored path.

### B5 — `CardDetector` + `IRectifier`
`Canny` → `findContours` `RETR_EXTERNAL` → filter on aspect **1 : 1.397** (63/88 mm) ±15%, widening to ±25% if detection misses, **and** minimum area → take top N by area → `getPerspectiveTransform` + `warpPerspective` to 488×680.

Log the discard reason for every rejected contour — you will need that log. Return **empty** when nothing card-shaped is present; never guess. Better to detect nothing than to hash a hand.

### B6 — Accuracy harness (in `Lab`) — the stream's real output
Over the fixture corpus, report **correct@1 / wrong@1 / no-match** per camera height, per difficulty rung, and the **distance-margin distribution** (true match vs best impostor). The margin is what sets `GoodDistance` and `OkDistance` — written to a **thresholds file committed next to the index**, which the app loads into `ScanSettings` at startup.

⚠️ **Measure on normal cards only.** Every basic-land art collapses to one oracle name, so retrieving the *wrong* Forest still scores correct@1 — lands inflate the metric with a category that structurally cannot fail. Capture them, run them as a smoke test, exclude them from the table.

### B7 — Synthetic frame generator (in `Lab`)
Scryfall image → downscale to the px/inch for a given height (**px/inch = 1360 / height_inches** for the C920) → mild keystone, Gaussian blur, sensor noise, JPEG artifacts. This is what CI runs on, because real card images can never be committed. Also lets the accuracy sweep extend past whatever heights were physically captured.

---

## Done when

- The round-trip gate passes: a Scryfall render retrieves itself at distance ≈ 0 through the full query path.
- **≥90% correct@1 on the real normal-card fixtures** at the chosen height.
- **wrong@1 ≈ 0** — failures are no-match, not confident-wrong. This matters more than the headline accuracy: a silent miss is recoverable, a confident wrong answer is permanent bad inventory.
- `GoodDistance` and `OkDistance` committed as measured values in the thresholds file, with the margin data behind them.
- `IOracleCatalog` returns every oracle card in the index.
- Detection returns 0 on an empty mat and the right count on a partial grid.
- The accuracy table is committed — it's the spine of the demo, not just an internal artifact.

## Fallbacks

- **Index build stalls on bandwidth:** hash a 5,000-card subset and proceed; scale later. The algorithm is what's being proven, not the corpus size.
- **Accuracy is poor at every height:** fall back to a candidate-list flow — surface the top 3 and let the confirmation grid resolve it — rather than chasing a single-answer match. The UI stream already supports ranked candidates, so this costs no rework.
- **Detection is unreliable:** widen the aspect tolerance to ±25% first, then revisit mat contrast (see risks) before touching the algorithm.

---

## What a reviewer should scrutinise here

1. **Transform symmetry between index build and scan.** Is it genuinely one shared function, or two code paths that currently agree? Two paths that agree today will diverge, and the failure is silent. This is the highest-value thing to check in this stream.
2. **Interpolation consistency** — `INTER_AREA` on both sides, at every resize, including inside the synthetic generator.
3. **The `0.85·width` region on non-standard cards.** Full-art lands and borderless cards don't have a type line where the region expects one. Is that handled, or just accepted and documented?
4. **Is `wrong@1` actually being measured**, or is the harness only reporting accuracy? An accuracy-only harness hides the failure mode that matters.
5. **Are lands excluded from the accuracy table** — and is that exclusion enforced in code rather than remembered?
6. **Detection false positives** — is the discard reason logged, and are both aspect *and* area filters applied?
7. **Does the identifier filter by threshold?** It must not — thresholds belong to the pipeline.
8. **Is the CardSpotter attribution present** in `THIRD-PARTY-NOTICES`? BSD-3-Clause requires the notice; omitting it is a licence violation, not a nicety.

## Risks owned by this stream

1. **Reference/query transform divergence** — see B2. Mitigated structurally, not by discipline.
2. **Glare, focus and tilt destroy the local-median bit pattern.** These are the top items in Wizards' own SpellTable troubleshooting list. Physical mitigation (diffuse off-axis light), but this stream is where it shows up as numbers.
3. **Mat contrast.** Detection depends on finding the card edge, and modern cards are black-bordered, so a *dark* mat is the worst case — which is what the original plan recommended. Settle by measurement across light/mid/dark.
4. **Same-art printings are permanently indistinguishable.** Accepted scope (oracle name only), but the README must say so.
5. **Foils.** Glare defeats hashing without polarised or diffuse light. Out of v1 scope — document the failure rather than hide it.

---

## Plan review: research targets

For the pre-build stream review (see [`stream-review-directions.md`](stream-review-directions.md)). This is the stream where research matters most — check each against primary sources and record what you found.

1. **The seven steps against CardSpotter's actual source** (`github.com/relgin/cardspotter`). Blur kernel and σ, the 96 px width, `INTER_AREA`, the `0.85·width` region, the 32×32 resize, median-per-cell, per-cell distances, early rejection. Every mismatch between this doc and the source is a finding.
2. **Index size arithmetic.** This doc says ~67k × 1024 bits ≈ 8.6 MB; `CLAUDE.md` gives 53,482 unique artworks. Which count does `unique_artwork` actually return, and what does the index weigh?
3. **Scryfall bulk-data fields** — `jsonl_download_uri`, `compressed_size`, `image_status` values, and the `small` image size — against Scryfall's current API docs.
4. **`OpenCvSharp4` 4.13.0.20260627** — do `GaussianBlur`, `resize(INTER_AREA)`, `findContours`, `getPerspectiveTransform` and `warpPerspective` behave identically across `runtime.win` and `runtime.osx.arm64`? A platform difference in interpolation is a silent round-trip-gate failure.
5. **The seam.** Can the round-trip gate go "through the same code the scanner calls" using only `IRectifier` + `ICardIdentifier`, or does it need something the contract doesn't expose?
6. **Package set.** Stream 0 pins every package before the fork, and nothing can be added afterwards — name any package this stream would need (JSON, gzip, HTTP) that isn't listed.
