# Accuracy — crop-scale sensitivity (package B5c)

Measured on the Mac (arm64-darwin), 2026-09-22. `INTER_AREA` is not
bit-exact across x86-64/ARM64 (CLAUDE.md "The one gate that matters
most"), so the exact numbers below are provisional the same way
`data/index/thresholds.json`'s `referenceFloor` is — a win-x64 re-run
would land close but not identical. The *shape* of the curve and the
direction of every finding are architecture-independent; only the precise
distance values are not. Nothing here writes `goodDistance`/`okDistance`
(B6's job) or touches the committed index.

## Background

B5a/B5b found that on a dark mat, a black-bordered card's outer edge
merges into the mat, so `ContourCardDetector` finds a quad that lands on
the *inner* edge of the border — a crop-scale error, not a miss.
`plains_black` was the confirmed case: a confident wrong match (Mountain
at distance 345), while `solring_black` on the same mat showed no inset
and the best distance of the whole B5b run (94). B5c's job is to quantify
how much crop error the hash tolerates, and decide where the fix belongs.

## Part 1 — the curve (clean Scryfall renders)

Method: 150 non-land artworks sampled deterministically (seed
`20260922`, `RoundTripSampler.SelectUniformSample`) from the committed
index (`cards.lfidx`, SHA `6495314e…`). Each render is decoded once, then
`CropScaleTransform` applies a synthetic crop/inset (positive = detector
quad smaller than the true card, matching the observed defect direction;
negative = quad wider, approximated via border replication) before the
result is presented through the shipping query path
(`ICardIdentifier.Identify`) against the full 48,750-artwork index.
Reproducible via `lab crop-scale --cache <dir>`.

Basic lands are excluded (CLAUDE.md's Ladder: every Forest collapses to
the same name, which would inflate rank-1 without saying anything about
crop sensitivity).

| Crop scale | Rank-1 rate | Own-distance min/mean/median/max | Margin min/mean/median/max |
|---|---|---|---|
| 0.00 (no error) | 100.0% | 7 / 24.0 / 23 / 54 | 46 / 206.6 / 203 / 356 |
| +1% | 99.3% | 32 / 64.1 / 63 / 130 | 73 / 175.6 / 173 / 314 |
| +2% | 99.3% | 59 / 107.9 / 105 / 200 | 45 / 141.9 / 140 / 263 |
| +3% | 98.7% | 83 / 146.0 / 146 / 212 | 29 / 118.7 / 117 / 243 |
| +4% | 98.0% | 136 / 189.6 / 187 / 262 | 19 / 90.1 / 86 / 201 |
| **+5%** | **92.7%** | 147 / 230.4 / 230 / 303 | 5 / 66.3 / 65 / 197 |
| +6% | 75.3% | 164 / 261.8 / 263 / 332 | 1 / 47.3 / 43 / 186 |
| +8% | 31.3% | 202 / 299.8 / 296 / 376 | 1 / 34.2 / 28 / 166 |
| +10% | 16.0% | 239 / 330.9 / 326 / 395 | 0 / 23.5 / 12 / 127 |
| +15% | 4.0% | 299 / 330.8 / 327 / 359 | 2 / 28.0 / 21 / 92 |
| +20% | 2.0% | 360 / 363.3 / 363 / 367 | 5 / 17.3 / 10 / 37 |
| −2% (outset) | 99.3% | 42 / 106.7 / 108 / 164 | 54 / 131.8 / 128 / 292 |
| −5% (outset) | 92.0% | 117 / 200.4 / 200 / 273 | 4 / 54.6 / 49 / 208 |
| −8% (outset) | 28.7% | 153 / 241.0 / 245 / 353 | 1 / 33.1 / 22 / 169 |
| real (5%W / 3.5%H, B5b's plains_black estimate) | 98.0% | 138 / 191.7 / 191 / 259 | 10 / 89.6 / 87 / 214 |

**The curve is not flat.** Rank-1 rate holds at 98–100% through ±4%
isotropic crop error, breaks sharply between +4% (98.0%) and +6% (75.3%),
and collapses by +8% (31.3%). The outset (negative) direction degrades
similarly, roughly symmetric with the inset direction. **Rank-1 rate
starts to break at approximately 5–6% isotropic crop error** — this is
the number the package exists to produce.

One notable result: B5b's own by-eye anisotropic estimate for
`plains_black` (5% width / 3.5% height, less severe than an isotropic 5%
because height is less affected) still scores 98.0% rank-1 on a *clean*
render, with a comfortable margin (mean 89.6). On a clean render, this
specific crop error is **not by itself enough to cause a wrong match** —
see Part 2.

## Part 2 — the real-frame check

Ran the full detect→rectify→identify path against the 12 real C920
frames in `test-images/ad-hoc/` (gated on the folder's presence, same
pattern as `Tests/StreamB/RealCaptureGate.cs`).

**Measured inset, from real detector geometry rather than by eye:**
`plains_white` and `plains_brown` (same physical Plains card, correctly
detected on both mats per B5b's tally) average 266.0×372.6 px; the same
card on `plains_black` measures 239.2×347.0 px. That is **10.1% inset in
width, 6.9% in height** — worse than B5b's by-eye estimate of "~5%
width, 3–4% height," though the same direction and the same underlying
cause (the black border merging into the black mat).

**This resolves the apparent contradiction with Part 1.** The clean-render
curve at the anisotropic B5b estimate (5%/3.5%) still matched correctly
(98% rank-1, mean own-distance 191.7) — nowhere near the real observed
distance of 345 for `plains_black`. The *actual* measured inset is
larger (10.1%/6.9%), which the curve's own +8%/+10% rows put well past
the breaking point (16–31% rank-1) — consistent with a wrong match. The
remaining gap between the curve's predicted own-distance at that inset
(~330) and the real photo's rank-1 winner distance (345, a *different*
card) is real-world photographic degradation (lighting, focus — Risk 1)
compounding on top of the crop error, not crop error alone.

**`plains_black` baseline (no correction), reproducing B5b:**
top-1 = Mountain, distance 345; Plains not in the top 5.

**Correction sweep** (a compensating outset applied to the already-
rectified query card, at a small bracket of fractions):

| Correction | Top-1 | Plains distance |
|---|---|---|
| 0% (baseline) | Mountain, 345 | not in top 5 |
| −3% | Mise, 344 | not in top 5 |
| −5% | Foot Headquarters, 333 | 339 (not rank 1) |
| −8% | **Plains, 270** | 270 (rank 1) |
| −10% (≈ measured inset) | **Plains, 221** | 221 (rank 1) |

**A crop-scale correction recovers `plains_black`** when the compensating
fraction is close to the measured inset (−8% to −10%). Smaller
corrections (−3%, −5%) are insufficient — the compensating fraction has
to be in the right neighborhood, not merely nonzero.

## Part 3 — false-positive risk of a compensating sweep

The recovery above only shows a sweep *can* help the one known failure.
The identifier-sweep option's real risk is the opposite direction: does
trying extra scales make an **already well-framed** query worse? Every
extra scale can only lower a candidate's distance, never raise it, so a
well-framed query's own distance cannot improve from sweeping — the risk
is entirely that some *other* oracle's distance drops further and
overtakes it.

Measured directly: the same 150-sample well-framed (zero-crop-error)
population from Part 1, re-identified with a 3-scale sweep (`0%, −5%,
−10%`, matching the fractions that recovered `plains_black`) computing
the best distance per oracle across all three scales and both
orientations (mirroring `HashCardIdentifier`'s own per-oracle reduction,
generalized over scales — not a change to the frozen `ICardIdentifier`
interface, and not itself an `ICardIdentifier` implementation).

**Result: 0/150 flipped to a wrong rank-1.** Swept margin mean 201.9,
against the un-swept baseline's 206.6 — a negligible cost, not a
meaningful regression. On this evidence, the sweep is safe against the
well-framed population it must not disturb.

## Part 4 — performance cost

CLAUDE.md's own soft budget: 50 ms for 9 `Identify` calls against the
in-scope index shape (48,700 entries / 33,600 oracles), reproduced here
via `HashCardIdentifierPerformanceTests`.

| | Total (9 queries) | Per query |
|---|---|---|
| Baseline (no sweep) | 53.0 ms | 5.9 ms |
| 3-scale sweep (0%, −5%, −10%) | 295.3 ms | 32.8 ms |

**The 3-scale sweep breaches the 50 ms soft budget by ~6×, on a machine
where even the un-swept baseline is already over budget** (53.0 ms vs.
50 ms — itself a soft skip, not a hard failure, per that test's own
design). The measured sweep implementation is a direct, unoptimized
per-oracle reduction (a `Dictionary` + full sort, rather than
`HashCardIdentifier.RankOracles`'s flat-array bounded top-K insertion,
which is the actual optimization the shipped path uses for large
oracle tables); a production-quality sweep built on that same
optimization would cost meaningfully less than 295 ms, but the
dominant added cost is unavoidable and roughly linear in scale count —
hashing and scanning against 3 query variants instead of 1 is
irreducibly ~3× the per-query brute-force cost, and even a bare-minimum
2-scale version (0%, one compensating scale) would still land well
above the 50 ms budget on this hardware.

## Recommendation

**The curve is not flat — this is a real, measurable defect, not
anecdote.** Rank-1 rate breaks at roughly 5–6% isotropic crop error, and
`plains_black`'s real measured inset (10.1%/6.9%) sits well past that
break. A compensating crop-scale sweep both recovers the known failure
(at −8% to −10%) and, on the evidence gathered here, does not introduce
false positives on well-framed queries (0/150, negligible margin cost).

**This is exactly the situation the package's brief flags as a stop
condition:** the identifier-sweep option is the only fix this
investigation found evidence for, and it only works at a cost that
clearly and substantially breaches the performance budget (~6×,
confirmed measured, not estimated). Per the brief, that trade-off is
reported here rather than decided unilaterally — **no change has been
made to `HashCardIdentifier` or any other shipped code.** The three
options, with their evidence:

1. **Detector fix** (find the true outer edge on a dark mat) — not
   attempted. It is the structurally better fix (it fixes the cause, at
   query time, for free at identification time), but finding an edge
   with near-zero luminance contrast against the mat is a materially
   different, harder computer-vision problem than the current
   Canny/contour pipeline, and was out of this package's time budget.
   Recommended as a dedicated follow-up if the accuracy gain is worth a
   focused detector effort.
2. **Identifier sweep** — demonstrated to work and demonstrated safe
   against false positives, but demonstrated to cost ~6× the per-query
   budget on this hardware. Viable only if the user explicitly accepts
   that cost (or a scoped-down version of it — e.g. one compensating
   scale instead of two, which was not separately measured here but
   would cost proportionally less while still requiring the correction
   fraction to be in the right neighborhood, which the sweep in Part 2
   shows is not automatic).
3. **Neither** — not supported by the evidence. The curve is clearly not
   flat, and a real correction recovers a real failure; declining to fix
   anything is a defensible choice but not because the problem doesn't
   exist.

The user should decide between (1), a scoped/budgeted version of (2), or
explicitly accepting the current behavior, before any shipped code
changes.
