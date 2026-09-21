# Stream B — Identification

**The engine, and the only stream that can invalidate the project.** Everything else is plumbing around whatever this stream proves.

Owns (exclusive write access): `LoreFetch.Core/Identification/**`, `LoreFetch.Core/Imaging/**`, `LoreFetch.Lab/**`, `Tests/StreamB/**`, the stream B section of `README.md`, and the committed hash index + thresholds file
Consumes: `Core/Abstractions` (frozen — see [`CONTRACTS.md`](CONTRACTS.md)), the local fixture corpus
Implements: `ICardDetector`, `IRectifier`, `ICardIdentifier`, `IOracleCatalog`
Must not touch: `LoreFetch.App`, `LoreFetch.Capture`, `Core/Trigger`, `Core/Collection`, `Core/Export`, `Core/Scanning`, the fakes, any `.csproj`, `LoreFetch.slnx`

> [!NOTE]
> **Reconciled 2026-09-21.** Every proposal and open question below has been ruled on; the contract surface in [`CONTRACTS.md`](CONTRACTS.md) is now final and the rulings are recorded in [`RECONCILIATION.md`](RECONCILIATION.md). The *Plan review findings* section is kept as the review record — **read the disposition notes before acting on any recommendation there.** What changed for this stream:
>
> - **Both proposed contract changes accepted.** `CardCandidate` gained `string? ArtworkId`, so the round-trip gate asserts artwork identity through the public seam rather than via the concrete type. `Identify`'s `maxCandidates` now means nearest **distinct `OracleId`**.
> - **The asymmetry is confirmed and `CLAUDE.md` moved, not this doc.** Steps 2–3 are reference-side only; steps 4–6 are shared and must be bit-identical.
> - **The gate asserts a measured distance floor as a bound, not ≈ 0.** Record the floor; assert against it.
> - **Pull `normal` (~6.0 GB), not `small`** — confirmed by the user. `border_crop` is the fallback; never `small`.
> - **Index by kind, not by frame:** drop no-`image_uris`, `art_series`, `token` and non-English; keep all frames. Report accuracy on modern-frame fixtures only.
> - **Do not port the early rejection.** Threshold-keyed, inadmissible, and 0.243 ms brute force removes any argument for it.
> - **The index is built and committed on `win-x64`**, because `INTER_AREA` is not bit-exact across architectures. **Golden hashes run on the Windows leg only** — they carry `[Trait("Category","WindowsOnly")]` and the `macos-latest` leg filters them out. (Supersedes "both CI legs": `macos-latest` is ARM64, so a shared golden could only ever be red there.)
> - **`THIRD-PARTY-NOTICES` is fixed** — cited files corrected and the copyright verification completed. Nothing left for this stream to do there.

Needs no camera and no UI. Runs entirely on images on disk, on the Mac.

---

## What it implements

A C# port of **CardSpotter**'s 1024-bit perceptual hash (`github.com/relgin/cardspotter`, **BSD-3-Clause**, GPLv3-compatible, must be attributed in `THIRD-PARTY-NOTICES`). This is the algorithm Wizards' SpellTable actually ships — verified by inspecting its production WASM bundle, which contains no neural net.

Seven steps. ~~**The reference side and the query side must apply byte-identical transforms.**~~ **Corrected by review:** in the upstream source the two sides are *deliberately asymmetric* — steps 2–3 are **reference-side only**. See *Which steps run on which side* below, and **Open question 1**.

1. Rectify the card (perspective-correct from the detected quad) — query side
2. `GaussianBlur` 3×3, σ=1 — **reference side only**
3. Resize to **96 px wide**, then grayscale — **reference side only**
4. Take region `width × 0.85·width` — the **top ~61%**: title, art *and* type line — both sides
5. Resize that region to **32×32**, `INTER_AREA` — both sides
6. 4×4 grid of 8×8 cells; each bit = pixel **>** the median of its own cell, **plus a tie-break**: a pixel exactly *equal* to the median is also set when the median is > 128 → **1024 bits**
7. Match by full 1024-bit Hamming distance. Per-cell grid distances exist; **do not port the early rejection** (see B3)

Why it survives webcam frames where naive pHash fails: rectification removes distortion rather than tolerating it; blurring and downsampling the *reference* side destroys the high-frequency detail a webcam can't reproduce, so photo and render converge; the **local median per cell** makes each bit invariant to local brightness, exposure and white balance; the 4×4 grid lets glare corrupt some cells instead of failing the whole match.

That rationale only works *because* steps 2–3 are one-sided: blurring both sides equally does not make a render and a photo converge. The struck-through rule above contradicted the very paragraph justifying it.

### Which steps run on which side

Read from `Code/CardData.cpp` and `Code/QueryThread.cpp` at `relgin/cardspotter` (`VERSION.txt` = 3.12):

| | Reference (index build) | Query (scan) |
|---|---|---|
| Entry point | `CardData::BuildMatchData()` | `CardData::MakeHash()` called direct — `QueryThread.cpp:1873` |
| `GaussianBlur` 3×3 σ=1 | yes (`CardData.cpp:126`) | **no** |
| Resize to 96 px wide | yes (`CardData.cpp:130`) | **no** |
| Grayscale | after the 96 px resize | before rectification (`QueryThread.cpp:1459`) |
| Region `w × 0.85w` → 32×32 `INTER_AREA` | yes (`CardData.cpp:50–52`) | yes (same function) |

The only genuinely shared code is steps 4–6. **That is the function that must exist exactly once** — not the whole chain. Each side's own pre-step must also exist exactly once, and neither may change without rebuilding the index.

⚠️ **The 96 px resize in upstream is not `INTER_AREA`.** `CardData.cpp:130` passes `cv::INTER_AREA` as the **4th positional argument** of `cv::resize`, which is `double fx`. The signature is `resize(src, dst, dsize, fx = 0, fy = 0, interpolation = INTER_LINEAR)`, so the flag lands in `fx` as `3.0` — ignored, because `dsize` is non-empty — and the resize silently runs **`INTER_LINEAR`**. Only the 32×32 resize (`CardData.cpp:52`, full 6-argument form) is really `INTER_AREA`. LoreFetch should use `INTER_AREA` for the 96 px step *deliberately*, since it is the right filter for a ~5× downscale and OpenCV recommends it for shrinking — but this doc must not claim upstream does so, and a port that "faithfully" copies the call gets `INTER_LINEAR` by accident.

⚠️ **`median()` is not the textbook median.** `CardData.h:12–26` returns `nth_element(n/2)` — the **upper** order statistic — and only averages the two middle elements when `n` is *odd*, which is the inverse of the usual definition. Cells are always 8×8 = 64, so the odd branch never runs and the value is simply the 33rd smallest of 64. A port that calls a library `Median()` helper will average elements 32 and 33 for even `n`, land half a level off, and — via the step-6 tie-break — flip bits in every flat cell. Take the upper order statistic explicitly.

---

## Tasks, in order

### B1 — Port the hash
Implement per the seven steps. Test the invariants **and** commit golden hashes:
- ~~the same image at two input scales produces the same hash~~ — **not an invariant.** `INTER_AREA` from 488 px and from 976 px to 96 px are different box filters, so bits whose cell value sits near the median will flip. Assert *Hamming distance below a small measured bound*, never equality.
- a global brightness *offset* leaves the local-median bits stable — true, because the median shifts with it. A **gamma** shift is order-preserving so `v > m` survives, but the step-6 tie-break tests `m > 128` against a fixed constant and is **not** gamma-invariant. Same treatment: a bound, not equality.
- an inverted image does *not* match — strong invariant. Inversion flips every non-tied comparison, so expect distance ≈ 1024.
- **Golden hashes, committed.** The doc's original "invariants rather than golden values" is the wrong call for the one risk this stream names as its highest. Invariants pass just as happily against a transform that drifted; a committed expected 1024-bit hash for a handful of **synthetically generated** inputs (generatable, so committable — no imagery) is the only test that fails loudly when either side's chain changes. Golden hashes are also the natural home for the cross-platform check in Risk 6.

### B2 — The round-trip gate ⚠️ do this before anything builds on the hash
~~**A Scryfall render must retrieve *itself* at Hamming distance ≈ 0 through the full query path.**~~ **Restated by review.** Distance ≈ 0 is the wrong target, and asserting it would force the two sides symmetric, destroying the reference-side blur that makes the algorithm work at all (see *Which steps run on which side*). The gate is:

1. A Scryfall render pushed through the **query** path retrieves **its own index entry at rank 1** — identified by *artwork*, not merely by `OracleId`.
2. The rank-1 distance is **small and stable**, and its measured value is recorded as the **reference floor** in the thresholds file. Every later accuracy number is read against that floor. If the floor moves between runs, a transform changed.
3. The margin to the best *different-artwork* entry is recorded alongside it.

⚠️ **Point 1's "by artwork" is load-bearing.** `unique_artwork` carries 54,951 usable artworks across only 37,926 distinct `oracle_id`s — a heavily reprinted card has a dozen arts sharing one `OracleId`. `ICardIdentifier.Identify` returns `CardCandidate(OracleId, OracleName, Distance)` and **cannot distinguish them**, so a gate written against the public seam passes when the render matches a *completely different art of the same card* — precisely the silent divergence the gate exists to catch. Assert against an artwork-level identifier on the concrete `Core/Identification` type; the interface stays as frozen. (See **Proposed contract change 1** for the alternative.)

If the two sides diverge — a different interpolation, a different region offset, a stray colour conversion — **matching degrades silently instead of failing loudly.** That is the single highest-risk failure mode in the project, cheap to detect here and expensive at integration.

The structural defence: **the index builder and the scanner share one implementation of steps 4–6**, and each side's own pre-step exists exactly once. Not two implementations that agree; one function, called twice. Pair it with the committed golden hashes from B1 — a shared function still drifts, and only a golden value notices.

### B3 — `HashIndex`
Full brute-force Hamming matching. **~~microseconds~~ → measured 0.243 ms** per query over 55k × 1024 bits (optimised C, hardware popcount; 2.19 ms for a 9-card cohort). That is ~250× the doc's original claim but *strengthens* the conclusion: no ANN structure is needed, **and no early rejection is needed either**. Returns a **ranked** candidate list; the accuracy harness needs rank-N to compute distance margins.

`ICardIdentifier.Identify` **never filters by threshold** — it returns the nearest `maxCandidates`, always. The scan pipeline applies `GoodDistance`/`OkDistance` in one place.

⚠️ ~~(Early rejection inside the search is fine: it prunes cards that can't make the top N, not cards beyond a threshold.)~~ **Wrong on both counts — do not port it.** Upstream's early rejection is threshold-driven and inadmissible:

- `QueryThread.cpp:660–664` prunes on `QuickHammingDistance > quickCap`, where `quickCap = myOkMatchScore / 18` and `QuickHammingDistance` is the 64-bit distance of **grid cell (1,1) alone** (`CardData.h:81–84`). One cell out of sixteen is no lower bound on the full distance, so this **can and does discard the true best match**.
- `QueryThread.cpp:676–684` then breaks out of every loop as soon as `bestHamming < myGoodMatchScore`, returning the **first good-enough** card rather than the best.

Both are keyed to `myGoodMatchScore` / `myOkMatchScore` — i.e. exactly the thresholds `Identify` is contractually forbidden to apply — and both destroy the ranking the accuracy harness and the runner-up menu depend on. At 0.243 ms for the exhaustive scan there is nothing to buy. Keep `GetGridDistance` as a **diagnostic** (per-cell distances tell you *which* cells glare wrecked); never as a filter.

**Useful upstream anchors for B6.** Defaults are `myGoodMatchScore = 170` and `myOkMatchScore = 270` out of 1024 (`QueryThread.cpp:152–153`). Independently, the minimum distance from a query to 55k *random* 1024-bit vectors measures ≈ 433, so **any measured `OkDistance` above ~430 is indistinguishable from noise** at this index size. 170/270 sit comfortably below that floor; a calibration landing far above it means something is wrong upstream of the search.

⚠️ **Rank semantics are unspecified.** `Identify` returns the `maxCandidates` nearest — nearest *index entries* or nearest *distinct oracle cards*? With ~1.45 arts per oracle card on average (and far more for staples), the top 5 entries can all be the same card, which makes the runner-up menu useless and makes B6's "best impostor" margin measure art-vs-art of the *same* card rather than a real confusion. Collapse to distinct `OracleId`, keeping the best distance per card. See **Proposed contract change 2**.

`IOracleCatalog` is implemented from the same index: every distinct `(OracleId, OracleName)` it carries.

### B4 — Index builder (maintainer tool, in `Lab`)
From Scryfall bulk data — all figures below re-measured against the live API on 2026-09-21:
- Resolve via `GET https://api.scryfall.com/bulk-data/unique_artwork`. ✅ Verified: returns HTTP 200, `jsonl_download_uri` and `compressed_size` both present, the old `download_uri`/`size` fields are indeed gone, and the filename carries a daily timestamp (`unique-artwork-20260921090227.jsonl.gz`, 37,951,328 bytes). Never hardcode it.
- ⚠️ **Pull `normal`, not `small`.** `small` is **146 × 204** px; the canonical `RectifiedCard` is **488 × 680**, which is exactly Scryfall's `normal`. Building the index from `small` means the reference side's 96 px step is a 1.5× downscale while the query side's is 5.1× — different box filters over different source detail, so the two sides can never converge and the B2 floor is permanently and invisibly inflated. `normal` makes reference and query start from identical geometry, which is the whole reason `CONTRACTS.md` picked 488 × 680.
  **Cost of the correction:** `small` averages 14.3 KB → ~0.79 GB total (the doc's ~0.74 GB was right for `small`). `normal` averages 109.6 KB → **~6.0 GB**, about 7.7× the transfer. `*.scryfall.io` file origins are **unmetered**, so this is wall-clock and disk, not rate limit. See **Open question 2** if 6 GB is unacceptable.
- ✅ `*.scryfall.io` is unmetered — verbatim: *"The direct file origins located at `*.scryfall.io` do not have rate limits."* Images are served from `cards.scryfall.io`, bulk from `data.scryfall.io`; both are covered.
- ⚠️ **Rate limits, corrected:** it is **not** "2/sec on `/cards/*`". The 2/sec limit applies to `/cards/search`, `/cards/named`, `/cards/random` and `/cards/collection` only; **`/cards/manifest` is 10/minute**; everything else — including `/bulk-data/*` — is 10/sec. A 429 locks access for 30 s and *"It is not acceptable to ignore HTTP 429 responses."* The builder makes exactly one `api.scryfall.com` call, so this only matters if it ever grows a per-card fallback.
- `User-Agent: LoreFetch/0.1 (github.com/jnapoli87/LoreFetch)` **and** `Accept` are both mandatory. ✅ Verified, verbatim: *"All HTTP requests to api.scryfall.com must include a User-Agent header and an Accept header… Do not allow HTTP libraries to choose the header for you."*
- Filter on `image_status` — skip `placeholder` and `missing`. ✅ The field has exactly four values: `missing`, `placeholder`, `lowres`, `highres_scan`. Note this filter is nearly a no-op today: the live file is 53,240 `highres_scan`, 1,711 `lowres`, **12 `placeholder`, zero `missing`**. Keep it — new sets land as `missing` — but it is not what shapes the index.
- ⚠️ **The filter that actually matters is missing from this plan.** Of 54,963 objects: **3,440 have no top-level `image_uris`** (multi-faced cards, images live per face) and will crash or silently vanish in a naive builder — skip them explicitly, since scope is single-faced. Then 2,487 `art_series` and 1,537 `token` layouts, plus `memorabilia`/`token` set types, pollute both the index and the `IOracleCatalog` type-ahead with things that are not cards. Cascade, measured:

  | Filter | Arts | Distinct `OracleId` |
  |---|---|---|
  | raw `unique_artwork` | 54,963 | 37,926 |
  | `image_status` ok | 54,951 | 37,926 |
  | + `lang == "en"` | 54,550 | |
  | + has `image_uris.normal` | 51,110 | |
  | + drop token/art_series/emblem/scheme/planar/vanguard layouts | 49,082 | |
  | + drop token/memorabilia/art_series/minigame set types | **48,713** | **33,578** |
  | + `frame == "2015"` (the stated modern-frame scope) | 31,684 | 21,534 |

- ⚠️ **Output size, corrected.** ~~**~8.6 MB** (1024 bits × ~67k)~~ — **67k is not the count of anything.** `unique_artwork` returns **54,963** objects, 48,713 after the scope filter. 1024 bits = 128 bytes, so the hash payload is **6.0 MiB** filtered (6.7 MiB unfiltered), not 8.6 MB. The ~8.6 MB figure is only reached by *including* the name table — 33,578 distinct `(OracleId, OracleName)` pairs at ~17.9 bytes of name each ≈ 1.3 MiB, giving **~8.2 MiB total**. Coincidentally close, but the stated derivation was wrong. Either way it is comfortably committable.
- **Never commit the downloaded images.** Cache them outside the repo or in a gitignored path. At `normal` that cache is ~6 GB — make sure the path chosen is genuinely gitignored *and* not inside a worktree.

### B5 — `CardDetector` + `IRectifier`
`Canny` → **morphological close** → `findContours` `RETR_EXTERNAL` → **`approxPolyDP` to 4 points** → **order the corners TL, TR, BR, BL** → filter on aspect **1 : 1.397** (63/88 mm, = 1.3968 ✅) ±15%, widening to ±25% if detection misses, **and** minimum area → take top N by area → `getPerspectiveTransform` + `warpPerspective` to 488×680.

⚠️ **Three steps were missing from the chain**, each of which stops it compiling or stops it working:
1. **`approxPolyDP`.** `getPerspectiveTransform` needs exactly 4 points; a raw contour has hundreds. Without polygon approximation (or `minAreaRect`) there is no quad to hand it. Reject any contour that does not approximate to 4 points — that is a cheap, high-value discard reason in its own right.
2. **Corner ordering.** `CardQuad` is contractually *"always ordered TL, TR, BR, BL"*, but `findContours` returns points in traversal order from an arbitrary start. Sort by angle about the centroid, then rotate so the top-left is first. Get this wrong and `warpPerspective` silently delivers a rotated or mirrored card, which reads downstream as a hash miss rather than a geometry bug.
3. **The `warpPerspective` interpolation flag.** Unspecified in the original text, and its default is `INTER_LINEAR`. Since the rectified card *is* the hash's input, an unstated default is exactly the silent-divergence vector B2 exists to catch — pin it explicitly.
   ⚠️ And note **`INTER_AREA` cannot be used here**: OpenCV documents `warpPerspective`'s flags as *"`INTER_LINEAR` or `INTER_NEAREST`"*, and `INTER_AREA` is listed as *"not supported by this function"*. So this stream's "`INTER_AREA` on both sides, at every resize" goal is unachievable at the warp and must be stated as what it really is — `INTER_AREA` at every *resize*, a pinned `INTER_LINEAR` at the *warp*.

⚠️ **Card orientation is unhandled.** A card laid on the mat 180° round produces a perfectly valid quad — "TL" is geometric, not semantic — so rectification cannot tell upright from upside-down, and an upside-down card simply will not match. Upstream treats this as mandatory: `generateUpsideDown = true` (`QueryThread.cpp:1865`) and `cv::flip(card, card, -1)` (`CardData.h:207`). **Hash both orientations per detected card and keep the better match.** Cost is one extra 0.243 ms search.

⚠️ **Crop-scale alignment is the unowned risk here.** The hash is sensitive to how much card border the quad includes, and the detector's Canny edge will not sit where the Scryfall render's framing does. Upstream compensates hard: it expands the rect 11%, then sweeps **7 crop scales** (0.76–0.94 of that) × 2 orientations, twice over — **28 hashed variations per candidate rect** (`QueryThread.cpp:1859–1874`, `GetRectVariations` at 1645). This plan hashes exactly one. LoreFetch's detector-derived quad is tighter than a screen-scrape `RotatedRect` so it should need far fewer, but "one, and no orientation variants" is optimistic and is a likely cause if B6 accuracy comes in low. Measure the distance floor as a function of crop scale early — it is the cheapest experiment in the stream — and add a small sweep if the curve is sharp.

Log the discard reason for every rejected contour — you will need that log. Return **empty** when nothing card-shaped is present; never guess. Better to detect nothing than to hash a hand.

### B6 — Accuracy harness (in `Lab`) — the stream's real output
Over the fixture corpus, report **correct@1 / wrong@1 / no-match** per camera height, per difficulty rung, and the **distance-margin distribution** (true match vs best impostor). The margin is what sets `GoodDistance` and `OkDistance` — written to a **thresholds file committed next to the index**, which the app loads into `ScanSettings` at startup.

⚠️ **Measure on normal cards only.** Every basic-land art collapses to one oracle name, so retrieving the *wrong* Forest still scores correct@1 — lands inflate the metric with a category that structurally cannot fail. Capture them, run them as a smoke test, exclude them from the table.

**Enforce that exclusion in code, not in the fixture naming convention.** Filter on the index record's `type_line` starting with `Basic Land` — that is derivable from data the builder already reads, so it cannot rot the way a remembered "don't count the lands folder" rule does. The harness should print the excluded count next to the table, so an exclusion that silently stops matching is visible.

⚠️ **`wrong@1 ≈ 0` is not a testable criterion** (see *Done when*). `wrong@1` is only defined once a threshold is chosen, and the threshold is itself derived from this harness's margin data — so state it as a number against a stated `OkDistance`, e.g. *"at the calibrated `OkDistance`, wrong@1 ≤ 1 of N"*, and have the harness fail rather than report when it is exceeded. Also assert `correct@1 + wrong@1 + no-match = 100%`; a harness whose buckets don't sum is silently dropping fixtures.

### B7 — Synthetic frame generator (in `Lab`)
Scryfall image → downscale to the px/inch for a given height (**px/inch = 1360 / height_inches** for the C920) → mild keystone, Gaussian blur, sensor noise, JPEG artifacts. This is what CI runs on, because real card images can never be committed. Also lets the accuracy sweep extend past whatever heights were physically captured.

✅ **The formula checks out.** 78° diagonal on 16:9 → half-width = tan(39°) × 16/√(16²+9²) = 0.7058 → HFOV 70.4°, VFOV 43.3°; 1920 / (2 × 0.7058) = **1359.5**, so `1360 / height_inches` is right.

Two notes on using it:
- At 9.75″ that is 139.4 px/inch, so a card is **~349 × 488 px** — and it is then warped **up** to the 488 × 680 canonical size. The query side therefore upscales where the reference side downscales from 488. That asymmetry is real but it is also exactly what the physical camera does, so keep it; it is another reason the B2 floor is not 0.
- ⚠️ **The generator must not be a third transform.** It has to call the same rectify-and-hash path as the scanner, or CI is validating a pipeline that does not ship. Its own resizes are `INTER_AREA` for downscale; its keystone warp takes the same pinned flag as B5's.
- ⚠️ **No fixture generates an empty mat**, yet *Done when* requires "detection returns 0 on an empty mat". Add a bare-mat generator (mat texture, no card) across the light/mid/dark contrasts of Risk 3 — otherwise that criterion cannot run in CI at all, which is where it matters most, since false positives on an empty table are what produce phantom cohorts.

---

## Done when

- The round-trip gate passes: a Scryfall render retrieves **its own artwork entry at rank 1** through the query path, and the distance floor is recorded. ~~at distance ≈ 0~~ — see B2.
- **≥90% correct@1 on the real normal-card fixtures** at the chosen height.
- ~~**wrong@1 ≈ 0**~~ → **wrong@1 ≤ an explicit count at the calibrated `OkDistance`**, asserted by the harness. "≈ 0" cannot pass or fail. Failures should be no-match, not confident-wrong. This matters more than the headline accuracy: a silent miss is recoverable, a confident wrong answer is permanent bad inventory.
- **Golden hashes committed, generated on `win-x64`, and green on the Windows leg** — traited `WindowsOnly` and filtered out on macOS (see Risk 6). ~~identical on both CI legs~~: `INTER_AREA` is not bit-exact on ARM64, so that was unachievable by construction.
- `GoodDistance` and `OkDistance` committed as measured values in the thresholds file, with the margin data behind them.
- `IOracleCatalog` returns every oracle card in the index.
- Detection returns 0 on an empty mat and the right count on a partial grid.
- The accuracy table is committed — it's the spine of the demo, not just an internal artifact.

## Fallbacks

- **Index build stalls on bandwidth:** hash a 5,000-card subset and proceed; scale later. The algorithm is what's being proven, not the corpus size.
  ⚠️ But **accuracy measured against a 5k index is not comparable to the full index** and must not be quoted as the headline number. The impostor pool is ~10× smaller, so correct@1 is optimistic and the distance margin is inflated — thresholds calibrated on 5k will be too loose at 49k. Label any such table with its index size, and recalibrate before the number goes in the README.
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

1. **Reference/query transform divergence** — see B2. Mitigated structurally, not by discipline. Note the mitigation is narrower than "one shared function": only steps 4–6 are shared, so each side's pre-step needs its own guard, which is what the B1 golden hashes are for.
2. **Glare, focus and tilt destroy the local-median bit pattern.** These are the top items in Wizards' own SpellTable troubleshooting list. Physical mitigation (diffuse off-axis light), but this stream is where it shows up as numbers.
3. **Mat contrast.** Detection depends on finding the card edge, and modern cards are black-bordered, so a *dark* mat is the worst case — which is what the original plan recommended. Settle by measurement across light/mid/dark.
4. **Same-art printings are permanently indistinguishable.** Accepted scope (oracle name only), but the README must say so.
5. **Foils.** Glare defeats hashing without polarised or diffuse light. Out of v1 scope — document the failure rather than hide it.
6. **⚠️ `INTER_AREA` is not bit-exact across x86-64 and ARM64** — new, found in review. On AArch64 `cv::resize` dispatches to NVIDIA's carotene NEON HAL, which supplies its own `INTER_AREA` kernel and is on by default for ARM builds; OpenCV has a *confirmed* bug that carotene rounds differently from the generic path ([#24163](https://github.com/opencv/opencv/issues/24163)) and closed a cross-platform resize-mismatch report as won't-fix ([#22477](https://github.com/opencv/opencv/issues/22477)). Nothing in OpenCV documents `INTER_AREA` as bit-exact — only `INTER_LINEAR_EXACT` and `INTER_NEAREST_EXACT` carry that claim. **So an index built on the Mac may not match queries hashed on Windows**, presenting exactly as "degrades silently", the project's stated top risk.
   *Good news, same research:* `GaussianBlur` 3×3 σ=1 on `CV_8U` is **safe**. Its IPP and HAL escapes both sit behind `hint == ALGO_HINT_APPROX`, `ALGO_HINT_DEFAULT` resolves to `ALGO_HINT_ACCURATE` unless set at build time (OpenCvSharp does not set it), and passing σ explicitly fails the binomial-HAL gate — so it runs OpenCV's bit-exact fixed-point path. Hamming distance via `BitOperations.PopCount` is bit-exact by definition.
   **Mitigation:** build and commit the index on the **same architecture that ships** (`win-x64`), and add a golden-hash equality test that runs on *both* CI legs so a divergence fails loudly instead of degrading. `Cv2.GetBuildInformation()` reports the "Used HAL" line and is the definitive check on what the `osx.arm64` binary actually contains.
7. **Card orientation** — an upside-down card cannot match without hashing both 180° orientations. See B5.

---

## Plan review: research targets

For the pre-build stream review (see [`stream-review-directions.md`](stream-review-directions.md)). This is the stream where research matters most — check each against primary sources and record what you found.

1. **The seven steps against CardSpotter's actual source** (`github.com/relgin/cardspotter`). Blur kernel and σ, the 96 px width, `INTER_AREA`, the `0.85·width` region, the 32×32 resize, median-per-cell, per-cell distances, early rejection. Every mismatch between this doc and the source is a finding.
2. **Index size arithmetic.** This doc says ~67k × 1024 bits ≈ 8.6 MB; `CLAUDE.md` gives 53,482 unique artworks. Which count does `unique_artwork` actually return, and what does the index weigh?
3. **Scryfall bulk-data fields** — `jsonl_download_uri`, `compressed_size`, `image_status` values, and the `small` image size — against Scryfall's current API docs.
4. **`OpenCvSharp4` 4.13.0.20260627** — do `GaussianBlur`, `resize(INTER_AREA)`, `findContours`, `getPerspectiveTransform` and `warpPerspective` behave identically across `runtime.win` and `runtime.osx.arm64`? A platform difference in interpolation is a silent round-trip-gate failure.
5. **The seam.** Can the round-trip gate go "through the same code the scanner calls" using only `IRectifier` + `ICardIdentifier`, or does it need something the contract doesn't expose?
6. **Package set.** Stream 0 pins every package before the fork, and nothing can be added afterwards — name any package this stream would need (JSON, gzip, HTTP) that isn't listed.

---

## Plan review findings — 2026-09-21

Research targets 1–6 were worked against primary sources: the CardSpotter source tree, the live Scryfall API and its docs, the OpenCV 4.x headers, the NuGet flat-container indexes and the OpenCvSharp `4.x` branch. Corrections are applied inline above; this section records what was checked.

### Verified

**Target 1 — the seven steps against CardSpotter's source** (`github.com/relgin/cardspotter`, `VERSION.txt` = 3.12):
- `GaussianBlur` kernel **3×3, σ=1** — `Code/CardData.cpp:126`, `cv::GaussianBlur(myDisplayImage, myDisplayImage, cv::Size(3,3), 1)`. σ=1 applies to both axes: OpenCV's `sigmaY` defaults to 0 and *"if sigmaY is zero, it is set to be equal to sigmaX"* — https://github.com/opencv/opencv/blob/4.x/modules/imgproc/include/opencv2/imgproc.hpp (GaussianBlur docs). The same docs recommend specifying `sigmaY` explicitly "to fully control the result regardless of possible future modifications" — worth doing here for exactly that reason.
- **96 px target width** — `CardData.cpp:128`, `const int targetSize = 96`, with the height scaled proportionally. ✅
- **Region `width × 0.85·width` from the top-left** — `CardData.cpp:50`, `cv::Rect(0, 0, cols*1.0, cols*1.0*0.85f)` (`offset = 0.0f`, `part = 1.0f`). ✅ And **~61%** is right: 0.85w / 1.397w = **60.8%** of card height. ✅
- **32×32 resize with `INTER_AREA`** — `CardData.cpp:52`, `cv::resize(inputsub, myIcon, cv::Size(32,32), 0, 0, cv::INTER_AREA)`, correct 6-argument form. ✅
- **4×4 grid of 8×8 cells → 1024 bits** — `CardData.h:63–67`: `BITS=32`, `GRID=4`, `CELLBITS=32/4=8`, and `myHash32[32]` × 32 bits = 1024. 16 cells × 64 px = 1024. ✅ Each cell occupies exactly 2 consecutive `int32`s (`CELLINTS=2`), which is what makes per-cell distances possible — a port should map one cell to one `ulong`.
- **Per-cell grid distances exist** — `CardData.h:77–84`, `GetGridDistance(other, x, y)`. ✅
- **Median is per-cell, local** — `CardData.cpp:163`, computed over that cell's own 64 pixels. ✅ This is the brightness-invariance claim and it holds.
- **Upstream threshold defaults** — `QueryThread.cpp:152–153`: `myGoodMatchScore(170)`, `myOkMatchScore(270)` out of 1024. A useful prior for B6.
- **CardSpotter is BSD-3-Clause, "Copyright (c) 2019, Jonas Gillberg"** — https://github.com/relgin/cardspotter/blob/master/LICENSE. This **confirms the copyright line already in `THIRD-PARTY-NOTICES`**, which carries a "⚠ VERIFY BEFORE v0.1.0" flag on it; that flag can now be cleared. (Not edited — not this stream's file. See Open question 5.)
- **No neural net** — the README describes it as *"extracting the clicked card using OpenCV and then finding the best match from a database using image hashing"* — https://github.com/relgin/cardspotter/blob/master/README.md. Consistent with the doc's claim about the algorithm class. The separate claim that *SpellTable* ships this algorithm was **not independently re-verified** in this review (it rests on inspecting SpellTable's WASM bundle); it is recorded as unverified, not as wrong.

**Target 2 — index arithmetic.** Measured by downloading and parsing the live `unique_artwork` file: **54,963 objects**, 54,951 after the `image_status` filter, **48,713** after a scope-appropriate filter, over **33,578 distinct `OracleId`s**. Hash payload 6.0 MiB filtered / 6.7 MiB unfiltered; ~8.2 MiB including the name table. Full cascade and the correction to "~67k / 8.6 MB" are in B4. `CLAUDE.md`'s 53,482 unique arts and 32,992 oracle names are *drift*, not error — both were plausible when measured; today's equivalents are 54,963 and 33,578.

**Target 3 — Scryfall bulk-data fields**, all against the live API and docs:
- `jsonl_download_uri` and `compressed_size` present; `download_uri`/`size` absent. `GET /bulk-data/unique_artwork` returns 200. Filenames timestamped daily. ✅
- `image_status` has exactly four values — `missing`, `placeholder`, `lowres`, `highres_scan` — https://scryfall.com/docs/api/images. ✅
- `small` is **146 × 204 JPG**; `normal` is **488 × 680 JPG** (also `large` 672×936, `png` 744×1040, `border_crop` 480×680), same page. ✅ `normal` matching `RectifiedCard`'s canonical size exactly is what drives the B4 correction.
- Both `User-Agent` and `Accept` mandatory, library-chosen UA forbidden — https://scryfall.com/docs/api. ✅
- `*.scryfall.io` file origins unmetered; 429 → 30 s lockout; *"It is not acceptable to ignore HTTP 429 responses"* — https://scryfall.com/docs/api/rate-limits. ✅ (The per-endpoint limits were **mis-stated** in this doc — see Corrected.)

**Target 4 — OpenCvSharp 4.13.0.20260627.**
- The package version **exists and is current**: https://api.nuget.org/v3-flatcontainer/opencvsharp4/index.json. `OpenCvSharp4.runtime.win` publishes the same version; `OpenCvSharp4.runtime.osx.arm64` has **exactly one published version ever**, that one — confirming `CLAUDE.md`'s caution. No `4.12.x` exists for the package (though OpenCV upstream *did* release 4.12.0 — shimat skipped it, so the trap as written is right about NuGet).
- All six APIs exist on the `4.x` branch with the expected names: `Cv2.GaussianBlur`, `Cv2.Resize` + `InterpolationFlags.Area`, `Cv2.FindContours` / `FindContoursAsArray` + `RetrievalModes.External`, `Cv2.GetPerspectiveTransform`, `Cv2.WarpPerspective`, `Cv2.Canny` — https://github.com/shimat/opencvsharp/blob/4.x/src/OpenCvSharp/Cv2/Cv2_imgproc.cs. ⚠️ Read the **`4.x`** branch: on `main` (5.0.x) `GaussianBlur`/`WarpPerspective` gained a trailing `AlgorithmHint` and `GetPerspectiveTransform` moved file, so `main` signatures won't match 4.13.
- **`GaussianBlur` 3×3 σ=1 on `CV_8U` is bit-exact across platforms** — its IPP path (*"IPP is not bit-exact to OpenCV implementation"*, https://github.com/opencv/opencv/blob/4.x/modules/imgproc/src/smooth.dispatch.cpp) and its HAL path both sit behind `hint == ALGO_HINT_APPROX`; `ALGO_HINT_DEFAULT` resolves to `ALGO_HINT_ACCURATE` unless set at build time, which OpenCvSharp does not do; and passing σ explicitly fails the `cv_hal_gaussianBlurBinomial` gate (which requires `sigma1 == 0 && sigma2 == 0`). The fixed-point bit-exact path runs instead.
- **`resize(INTER_AREA)` is *not* guaranteed bit-exact across architectures** — the one real platform risk. Recorded as new Risk 6 with sources.

**Target 5 — the seam. No contract change is required for the round-trip gate.** Both `CameraFrame` and `RectifiedCard` have public constructors with no pooling requirement (`pool` is nullable; `RectifiedCard` is plain managed memory), so this stream can build either from a JPEG on disk and drive `IRectifier` → `ICardIdentifier` exactly as the scanner does. The index builder can likewise construct a `RectifiedCard` per Scryfall `normal` render — already 488×680, so no warp needed — and call the identical hash function. A dummy `CardQuad` of (0,0)-(488,0)-(488,680)-(0,680) serves as `sourceQuad`; harmless. Two caveats, both handled above rather than by changing the contract: the gate must assert at *artwork* granularity (B2), and `PixelLayout` has no `Gray8`, so the hash does its own `cvtColor` internally (fine, and matches upstream).

**Target 6 — package set. No package is needed that Stream 0 wouldn't already add.** `HttpClient`, `System.Text.Json`, `System.Net.Http.Json` and `GZipStream` are all in the .NET 8/9 shared framework — https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview (*"built-in as part of the shared framework for .NET Core 3.0 and later"*), https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.gzipstream. Do **not** add the legacy `System.Net.Http` / `System.IO.Compression` NuGet packages; they are frozen netstandard-era shims. `System.Numerics.BitOperations.PopCount` is in-box since .NET Core 3.0 and lowers to `POPCNT` / `CNT`+`ADDV` — https://learn.microsoft.com/en-us/dotnet/api/system.numerics.bitoperations.popcount. **What Stream 0 must ensure:** `LoreFetch.Lab` and `Tests/StreamB` both reference `OpenCvSharp4` (non-`slim`, for `imgcodecs` JPEG decode) plus **both** runtime packages, since the index is built on one OS and the non-golden tests still run on both CI legs.

**Other claims checked and holding:** aspect 1:1.397 = 88/63 = 1.3968 ✅; `warpPerspective` to 488×680 matches `RectifiedCard.CanonicalWidth/Height` ✅; `px/inch = 1360 / height_inches` re-derived as 1359.5 ✅ (B7); `ICardIdentifier` must not threshold — consistent with `CONTRACTS.md` ✅; the ranked-candidate fallback is supported at the contract level (`CohortTile.Candidates` is *"ranked, unfiltered"*) ✅; CardSpotter attribution is present in `THIRD-PARTY-NOTICES` with credit, copyright, all three BSD conditions and the disclaimer ✅.

### Corrected

- **"The reference side and the query side must apply byte-identical transforms"** → they are **deliberately asymmetric** upstream. Steps 2–3 (blur, 96 px resize) run on the **reference side only**: the reference path is `BuildMatchData()` (`CardData.cpp:117–136`), while the query path calls `MakeHash()` directly (`QueryThread.cpp:1873`) on an already-grayscale rectified crop (`QueryThread.cpp:1459`) with no blur and no 96 px step. Only steps 4–6 are shared. The doc's own justifying paragraph — blurring *the reference* so render and photo converge — depends on that asymmetry, so the doc contradicted itself. — CardSpotter source, cited inline.
- **"Resize to 96 px wide, `INTER_AREA`"** → upstream's 96 px resize actually runs **`INTER_LINEAR`**. `CardData.cpp:130` passes `cv::INTER_AREA` as `resize`'s 4th positional argument, which is `double fx`, not `interpolation`; the signature is `resize(src, dst, dsize, fx=0, fy=0, interpolation=INTER_LINEAR)` and `INTER_AREA` = 3. — https://github.com/opencv/opencv/blob/4.x/modules/imgproc/include/opencv2/imgproc.hpp (resize declaration; `InterpolationFlags` enum). LoreFetch should use `INTER_AREA` deliberately, but not on the belief that upstream does.
- **"each bit = pixel > median of its own cell"** → there is also a **tie-break**: `blocks[i] = v > m || (std::abs(v - m) < 1 && m > 128)` (`CardData.cpp:167`). Since both are `int`, that second clause is `v == m && m > 128`. Not a rare branch — flat or blown-out cells have many pixels equal to the median — and omitting it changes many bits in exactly those cells.
- **`median()` is the upper order statistic, not the average of the two middle values**, and its parity test is inverted relative to convention (it averages only when `n` is *odd*; cells are 8×8=64 so that branch never runs) — `CardData.h:12–26`. A port using a library median helper will be half a level off and, through the tie-break above, flip bits in flat cells.
- **"Early rejection… prunes cards that can't make the top N, not cards beyond a threshold"** → **both halves are wrong.** Upstream prunes on the distance of **grid cell (1,1) alone** against `quickCap = myOkMatchScore / 18` (`QueryThread.cpp:638, 660–664`) — one cell of sixteen, no lower bound, so it can discard the true best match — and then breaks out of all loops at `bestHamming < myGoodMatchScore` (`QueryThread.cpp:676–684`), returning the first good-enough card rather than the best. Both are threshold-keyed, which `ICardIdentifier` forbids. **Recommendation: don't port it.**
- **"~67k × 1024-bit brute force is microseconds"** → **0.243 ms** measured for one query over 54,951 × 1024 bits (optimised C, hardware popcount), 2.19 ms for a 9-card cohort. ~250× the claim, but it *reinforces* "no ANN" and removes any argument for early rejection.
- **"~67k … ≈ 8.6 MB (1024 bits × ~67k)"** → `unique_artwork` returns **54,963** objects (48,713 in scope). Hash payload **6.0–6.7 MiB**; ~8.2 MiB only once the name table is counted. Measured against the live bulk file.
- **"Pull `small` images"** → **pull `normal`.** `small` is 146×204 while the canonical rectified card is 488×680 = Scryfall `normal`; building from `small` guarantees the two sides never converge. Cost rises from ~0.79 GB to ~6.0 GB (measured averages 14.3 KB vs 109.6 KB over a 12-image sample). — https://scryfall.com/docs/api/images
- **"only `api.scryfall.com` is rate-limited (2/sec on `/cards/*`)"** → 2/sec applies to `/cards/search`, `/cards/named`, `/cards/random`, `/cards/collection`; **`/cards/manifest` is 10/minute**; all other methods 10/sec. — https://scryfall.com/docs/api/rate-limits
- **`image_status` filter is nearly a no-op today** — the live file has 12 `placeholder` and **zero** `missing`. Keep it, but the filters that actually shape the index are the missing ones (face-only records, `art_series`, tokens) — added to B4.
- **B1's "same image at two input scales produces the same hash"** → not an invariant; `INTER_AREA` from different source sizes flips near-median bits. Assert a bound. Likewise the gamma invariant, which the `m > 128` tie-break breaks.
- **B5's chain was missing `approxPolyDP`, corner ordering, and the `warpPerspective` interpolation flag** — and **`INTER_AREA` cannot be used at the warp** at all: OpenCV documents `warpPerspective` flags as *"`INTER_LINEAR` or `INTER_NEAREST`"* and lists `INTER_AREA` as *"not supported by this function"* (imgproc.hpp, warpPerspective/remap docs). So "`INTER_AREA` on both sides at every resize" needed restating.
- **`wrong@1 ≈ 0` is not testable** — restated in *Done when* as a count against a stated `OkDistance`.
- **Lands exclusion had no enforcement mechanism** — now specified as a `type_line`-based filter with the excluded count printed.

### Proposed contract changes

- **`CardCandidate`**: add an artwork-level discriminator — e.g. `string ArtworkId` (Scryfall's printing `id`, already in the bulk record and free to carry). **Why:** `unique_artwork` holds 54,951 arts over 37,926 `OracleId`s, so `OracleId` cannot tell which reference entry matched. B2's round-trip gate must assert a render retrieves *its own art*; asserting only `OracleId` lets the gate pass while matching a different printing's art of the same card, which is the exact silent failure the gate exists to catch. A workaround exists (assert against the concrete `Core/Identification` type rather than the interface) and is what B2 now specifies, so **this is an optional improvement, not a blocker** — raised because it would also let the UI distinguish "same card, different art" runners-up.
  **Effect on other streams:** unknown — for reconciliation.
- **`ICardIdentifier.Identify`**: specify whether `maxCandidates` counts nearest *index entries* or nearest *distinct oracle cards*, and make it distinct-`OracleId` (best distance per card). **Why:** heavily reprinted cards have many arts, so an entry-ranked top-5 can be five arts of one card. That makes B6's "distance margin between true match and best impostor" measure art-vs-art of the same card instead of a genuine confusion — the margin is what calibrates `GoodDistance`/`OkDistance`, so the thresholds would be calibrated against the wrong quantity. This is a documentation/semantics change to a frozen interface, not a signature change.
  **Effect on other streams:** unknown — for reconciliation.

### Open questions

1. **Keep the reference/query asymmetry, or force the two sides symmetric?** Upstream is asymmetric (blur + 96 px on the reference only) and this doc's own rationale depends on that; but `CLAUDE.md` states the transforms must be identical, so the two disagree and one of them must move. **Recommendation: keep the asymmetry and amend the wording** — it is what upstream does, what the convergence rationale requires, and forcing symmetry would delete the mechanism that makes a webcam frame match a print render. The invariant worth enforcing is not "both sides identical" but "each side's transform exists exactly once and neither changes without an index rebuild", guarded by committed golden hashes. This also changes what B2 asserts: a small, recorded, stable distance floor instead of ≈ 0. Flagged because it edits a settled `CLAUDE.md` decision, which is not mine to change.
2. **Accept the ~6.0 GB image pull for `normal`, or keep `small` and accept a raised distance floor?** **Recommendation: pull `normal`.** The origin is unmetered so this is wall-clock and ~6 GB of scratch disk, not rate limit or cost, and it buys exact geometric agreement with `RectifiedCard` — the single cheapest reduction of the project's top risk. If 6 GB is genuinely impractical, the fallback is `border_crop` (480×680, near-identical geometry) rather than `small`; what must *not* happen is building from 146×204 and then wondering why the floor is high.
3. **Should the index be filtered to the stated v1 scope (modern frame, English, single-faced, non-token), or carry everything?** Filtering to `frame == "2015"` cuts the index from 48,713 to 31,684 arts and shrinks the impostor pool, which *raises* measured accuracy — but it also means a card outside scope returns a confidently wrong answer rather than no-match, and identification is opt-out. **Recommendation: index everything in scope-appropriate *kind* (drop tokens, art series, face-only, non-English) but keep all frames**, so an older card lands as a correct match rather than a confident impostor; then report accuracy on modern-frame fixtures only. That keeps the honest failure mode and the honest metric.
4. **Should B5 hash crop-scale variations, and how many?** Upstream needs 28 per rect (7 scales × 2 orientations × 2 passes). The 180° orientation pair is not optional and is now in B5. The scale sweep may be unnecessary given a detector-derived quad. **Recommendation: measure the distance floor against crop scale before deciding** — it is a one-afternoon experiment in `Lab` and it determines whether ≥90% correct@1 is reachable with one hash per card. Budget for a 3-scale sweep as the contingency.
5. **`THIRD-PARTY-NOTICES` needs two small edits this stream may not make.** The copyright line *"Copyright (c) 2019, Jonas Gillberg"* is now **confirmed correct** against the upstream LICENSE, so the "⚠ VERIFY BEFORE v0.1.0" block above it can be deleted. Separately, the file attributes the port to *"the hashing and matching logic in Code/CardData.cpp and Code/ImageHash.\*"* — **`Code/ImageHash.*` does not exist** in the repository; `ImageHash` is a struct declared in `Code/CardData.h` with `MakeHash` defined in `Code/CardData.cpp`, and the matching loop lives in `Code/QueryThread.cpp`. **Recommendation:** cite `Code/CardData.h`, `Code/CardData.cpp` and `Code/QueryThread.cpp`. Accuracy of the attribution is a BSD-3 obligation, so this is worth doing before v0.1.0 rather than at leisure.
