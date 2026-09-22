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

---

# Accuracy — real 3x3-grid detection: retrieval mode vs. mat contrast

Measured on the Mac (arm64-darwin), 2026-09-22, against six real C920
frames (`a_1.png`–`a_6.png`, 1920x1080, ~15" height, nine Final Fantasy-set
cards freehand on a white board), gitignored under `test-images/a_corpus/`
and never committed (CLAUDE.md "Never commit card imagery"). Reproducible
via `lab retrieval-experiment dump-contours <dir>` and
`lab retrieval-experiment run <dir>`.

## Background

`ContourCardDetector` found only 1–2 of 9 cards on every one of the six
frames under the shipped defaults. Three hypotheses competed: **H1**
(the nine cards are hierarchically nested under a larger contour —
`RETR_EXTERNAL` never returns a nested contour, so most cards are
invisible to it); **H2** (edge contrast — the cards' white/cream/coloured
borders against the white board defeat Canny); **H3** (decorative,
non-standard frames defeating `approxPolyDP` outright). A prior attempt
to test H1 by cropping the board's edge out of `a_1` moved it only from
1→2 accepted — weak support, and the orchestrator's working read going
into this package was that the mat is *not* the dominant cause.

## What was built

1. **`ContourDetectorOptions.RetrievalMode`** (`src/LoreFetch.Core/Imaging/ContourCardDetector.cs`):
   selects `RetrievalModes.External` (default, unchanged — CLAUDE.md's
   pinned decision) or `RetrievalModes.List`. `RunPipeline` now calls
   `Cv2.FindContours` with `_options.RetrievalMode` instead of a hardcoded
   `External`.
2. **`lab retrieval-experiment`** (`src/LoreFetch.Lab/RetrievalExperimentCommand.cs`):
   `dump-contours <dir>` prints every raw contour's bounding box (both
   retrieval modes) so per-frame crop rectangles could be picked from real
   geometry rather than by eye; `run <dir>` executes the 2x2 sweep below
   plus the identify pass on the best cell.
3. Two new tests in `Tests/StreamB/ContourCardDetectorRetrievalModeTests.cs`
   covering the flag: the default stays `External`, and `List` surfaces
   more raw contours than `External` on a synthetic nested frame (reusing
   `DetectorTestFrames.CardOnMat`'s outer-border/inner-frame-band/art-box
   shape) while the existing nested-duplicate dedupe still collapses it
   back to one card.

## The crop rectangles

**Per frame, not one shared rectangle** — `dump-contours`'s own `RETR_LIST`
output shows the nine-card cluster's bounding box shifts by up to ~150px
between frames even though the camera and board are fixed (the cards are
placed freehand, CLAUDE.md "no registration jig"). Each rectangle is that
frame's own card-cluster bounding box (read off `dump-contours`'s
card-sized contours: aspect ~0.70–0.75, area ~56k–75k), expanded 55px
left/right and ~15px top (top has almost no room to give: `a_1`'s own
`RETR_LIST` dump puts a large nested contour's top edge at y=94, just
16px above the top card row at y=110 — a fact about this rig's physical
layout, not something a crop can create space for) and extended to the
frame's own bottom edge, since none of the six frames shows the board's
bottom edge in-frame at all.

| Frame | Crop rect (x, y, w, h) |
|---|---|
| a_1 | 638, 95, 845×984 |
| a_2 | 525, 82, 873×997 |
| a_3 | 524, 74, 864×1005 |
| a_4 | 483, 73, 874×1006 |
| a_5 | 532, 73, 853×1006 |
| a_6 | 546, 60, 842×1019 |

**Zero `TouchesBorder` rejections attributable to the crop across all
six frames, in either retrieval mode** — the earlier attempt's specific
failure mode (clipping outer cards) did not recur.

## The 2x2 table

Accepted count `[quad sizes]`, then the rejection histogram. `full`/`crop`
is the frame; `External`/`List` is `RetrievalMode`.

**a_1** (crop 638,95,845×984)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 1 `[191x273]` — NotAQuad=14, NonConvex=8 | 8 `[217x323, 220x314, 216x303, 214x304, 214x303, 213x300, 197x275, 191x273]` — NotAQuad=522, MinArea=32, NonConvex=9, NestedDuplicate=7 |
| crop | 7 `[217x323, 220x314, 216x303, 214x304, 213x300, 197x275, 191x273]` — NotAQuad=3, NonConvex=1 | 8 (same sizes as full/List) — NotAQuad=493, MinArea=27, NestedDuplicate=7, AspectRatio=2, NonConvex=1 |

**a_2** (crop 525,82,873×997)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 1 `[211x291]` — NotAQuad=20, NonConvex=5 | 9 `[222x318, 222x317, 216x315, 216x300, 214x301, 213x301, 214x292, 210x298, 211x291]` — NotAQuad=674, MinArea=40, NestedDuplicate=9, NonConvex=6 |
| crop | 1 `[211x291]` — NotAQuad=3 | 9 (same sizes) — NotAQuad=619, MinArea=37, NestedDuplicate=9, NonConvex=1 |

**a_3** (crop 524,74,864×1005)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 1 `[220x312]` — NotAQuad=17, NonConvex=6, AspectRatio=1 | 8 `[218x316, 220x312, 216x299, 214x301, 211x297, 210x290, 196x282, 195x275]` — NotAQuad=622, MinArea=35, NestedDuplicate=8, NonConvex=7, AspectRatio=2 |
| crop | 1 `[220x312]` — NotAQuad=3, AspectRatio=1 | 8 (same sizes) — NotAQuad=580, MinArea=32, NestedDuplicate=8, AspectRatio=2, NonConvex=1 |

**a_4** (crop 483,73,874×1006)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 2 `[218x314, 215x300]` — NotAQuad=18, NonConvex=5, AspectRatio=1 | 9 `[218x314, 215x300, 214x301, 214x293, 209x296, 211x292, 201x294, 199x296, 195x286]` — NotAQuad=479, MinArea=29, NestedDuplicate=9, NonConvex=5, AspectRatio=1 |
| crop | 2 (same sizes) — NotAQuad=2, AspectRatio=1 | 9 (same sizes) — NotAQuad=436, MinArea=26, NestedDuplicate=9, AspectRatio=1 |

**a_5** (crop 532,73,853×1006)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 1 `[114x187]` — NotAQuad=37, NonConvex=7, MinArea=3, TouchesBorder=1 | 7 `[223x319, 217x322, 219x315, 216x300, 215x300, 214x302, 210x299]` — NotAQuad=581, MinArea=34, NestedDuplicate=9, NonConvex=8, AspectRatio=2 |
| crop | 2 `[223x319, 219x315]` — NotAQuad=21, MinArea=2, AspectRatio=1 | 7 (same sizes) — NotAQuad=543, MinArea=32, NestedDuplicate=9, AspectRatio=1, NonConvex=1 |

**a_6** (crop 546,60,842×1019)
| | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| full | 1 `[193x277]` — NotAQuad=19, NonConvex=6 | 8 `[219x314, 218x309, 216x301, 212x293, 212x290, 200x295, 196x287, 193x277]` — NotAQuad=612, MinArea=27, NonConvex=7, NestedDuplicate=3, AspectRatio=1 |
| crop | 1 `[193x277]` — NotAQuad=4 | 8 (same sizes) — NotAQuad=565, MinArea=26, NestedDuplicate=3, AspectRatio=1, NonConvex=1 |

The full-frame `External` column reproduces the six baseline measurements
this package started from exactly (191x273 / 211x291 / 220x312 / 218x314+215x300
/ 114x187 / 193x277, with matching reject histograms) — confirming the
tooling faithfully measures the shipped default before drawing any
conclusion from the other three cells.

**Totals across all 54 card-slots (6 frames × 9 cards):**

| Cell | Accepted |
|---|---|
| `full`/`External` (shipped default) | 7 / 54 (13%) |
| `crop`/`External` | 14 / 54 (26%) |
| `full`/`List` | 49 / 54 (91%) |
| `crop`/`List` | 49 / 54 (91%) |

## Nested dedupe under `RETR_LIST`: it activates, and it suppresses

B5a recorded `DedupeAndTakeTopN`'s `NestedDuplicate` branch as dead code
under `RETR_EXTERNAL`, reachable only via a hand-built unit test, because
`External` structurally never returns a nested contour. Under `List` it
is no longer dead: **every one of the twelve `run` cells above shows
`NestedDuplicate` in its rejection histogram** (3–9 per frame), and the
accepted counts stay sane (7–9, never 14–18) rather than double-counting
each card's outer border and its own inner frame/art-box edge as two
cards. The two new unit tests reproduce this on a synthetic frame and
confirm it directly: `List` finds strictly more raw contours than
`External` on `DetectorTestFrames.CardOnMat`'s nested shape, `External`
still shows zero `NestedDuplicate` rejections (the B5a-documented
control), and `List` shows at least one `NestedDuplicate` rejection while
still accepting exactly one card. **Nested dedupe activates under
`RETR_LIST` and it does suppress the inner quads it was written for.**

## Identify — best cell (`full`/`List`), frame `a_1`

`full/List` and `crop/List` tie at 49/54 overall; `full/List` is reported
(no cropping needed once the retrieval mode is fixed). Running the full
detect → rectify → identify path against the committed index, sorted
top-to-bottom/left-to-right against the known `a_1` layout (Freya
Crescent, Zidane Tantalus Thief, Adventurer's Airship / You're Not Alone,
Summon: Fat Chocobo, Instant Ramen / Cactuar, Cat Warriors, Defibrillating
Current):

| Position | Expected | Detected? | Top-1 | Distance | Verdict |
|---|---|---|---|---|---|
| row1col1 | Freya Crescent | yes | Freya Crescent | 104 | correct |
| row1col2 | Zidane, Tantalus Thief | **no** | — | — | **missed entirely** |
| row1col3 | Adventurer's Airship | yes | Adventurer's Airship | 208 | correct |
| row2col1 | You're Not Alone | yes | You're Not Alone | 135 | correct |
| row2col2 | Summon: Fat Chocobo | yes | Summon: Fat Chocobo | 149 | correct — see note below |
| row2col3 | Instant Ramen | yes | Instant Ramen | 178 | correct |
| row3col1 | Cactuar | yes | Cactuar | 82 | correct |
| row3col2 | Cat Warriors | yes | Plains | 288 | **wrong**, high distance |
| row3col3 | Defibrillating Current | yes | Urza's Power Plant | 272 | **wrong**, high distance |

8 of 9 detected, 6 of those 8 correctly identified. The one detection
miss is `Zidane, Tantalus Thief` — visibly the one card in the frame with
a light cream/tan border rather than black, i.e. exactly H2's low-contrast
case, surviving as a small residual failure even after `List` fixes the
nesting problem. The two wrong identifications both land at high distance
(272, 288 — well above the correct matches' 82–208 range), which is
exactly what `BestMatchDistance`-driven emphasis in the capture UI is for:
both would be highlighted for a second look, not silently accepted.

**Note on "Summon: Fat Chocobo" (a Saga):** the package brief flagged
this card as structurally unable to match — its art sits to the right of
the frame rather than in the hash's top-~61% region — and asked that a
failure here be reported separately rather than counted against the
result. It did **not** fail: it matched correctly at distance 149, mid-
pack among the correct matches. Both the reference render and the query
crop apparently carry enough shared structure in that top region (title
bar, Saga chapter-counter column, and this particular Saga's own top-of-
card art) for the hash to still converge. Reported as found, not forced
into either bucket the brief anticipated.

## Which hypothesis dominates

**H1 (retrieval mode / nesting) is the dominant cause.** Switching only
the retrieval mode, on the untouched full frame, took accepted detections
from 7/54 (13%) to 49/54 (91%) — a ~7x improvement with no cropping at
all. Cropping alone, still on `RETR_EXTERNAL`, helped far less and far
less consistently: 7→14/54 (13%→26%) overall, and looking at the per-frame
breakdown, almost all of that gain is concentrated in `a_1` (1→7) and
`a_5` (1→2); `a_2`, `a_3`, `a_4` and `a_6` show **no improvement at all**
from cropping under `External` (each stays at its full-frame count).
Cropping never adds anything on top of `List` either (49/54 both ways).

That `a_2`/`a_3`/`a_4`/`a_6` pattern is itself informative: even inside a
crop that removes the wall, cables and the board's own outline, `External`
still recovers only 1–2 of 9 cards there, with very few residual
`NotAQuad` rejections (2–4) — meaning most of the other 7–8 candidate
contours never even reach the quad-approximation step as separate
candidates at all. They are still nested under *something* inside the
crop, most plausibly the cards' own close physical adjacency: with only a
narrow band of white mat between rows/columns, Canny plus the 5x5
morphological close can bridge adjacent cards' black borders into one
merged region, which is exactly the shape `RETR_EXTERNAL` reports as a
single outer contour and never expands. This generalizes H1 from "the
board's outline nests the cards" (true for `a_1`, weakly, as the original
1→2 finding showed) to "insufficient white-mat gap between adjacent cards
plus whatever else is in the frame nests the cards" — different specific
geometry, same underlying `RETR_EXTERNAL` topology problem, and the same
fix (`RETR_LIST`) resolves both without needing to know which one is
active in a given frame.

**H2 (mat/edge contrast) is real but secondary.** Once nesting is fixed,
detection is not perfect: `a_1` still misses one card outright (the
lightest-bordered one, Zidane), and `a_5` caps at 7/9 under both `full`
and `crop`/`List` — two cards on that frame fail even with nesting
resolved. That is consistent with genuine contrast-driven `NotAQuad`
failures surviving on top of the nesting fix, at a small, non-dominant
rate (roughly 5/54 residual misses after `List`, versus ~47/54 recovered
by `List` itself).

**H3 (decorative/non-standard frames)** was not separately measured —
there was no need to: the near-total recovery under `List` (91%) leaves
little room for a distinct "frames defeat `approxPolyDP` regardless of
contrast" effect to be doing much work, though the residual 5/54 misses
could in principle include some.

## A parameter noticed, not touched

`ContourDetectorOptions.MorphCloseKernelSize` (5, unchanged) is a
plausible contributor to the `a_2`/`a_3`/`a_4`/`a_6` inter-card-merging
pattern above — a 5px square morphological close is enough to bridge a
narrow white gap between two adjacent card borders, which is exactly the
shape that would make `RETR_EXTERNAL` report neighbouring cards as one
merged outer contour. This is a hypothesis prompted by the measurement
above, **not itself measured** (no second sweep varying kernel size was
run), and per this package's constraint against tuning detector
parameters against these six frames, it was **not changed**. Flagged here
as a lead for whoever picks up the detector-fix option below, not as a
finding.

## Recommendation

**Switch the shipped default to `RETR_LIST`.** The evidence is a ~7x
improvement (7/54 → 49/54) from a single, already-implemented,
already-tested flag flip, with the pre-existing nested-duplicate dedupe
(B5a) confirmed to activate and correctly suppress the extra contours
`List` surfaces — this is not a new failure mode, it is exactly the
defence-in-depth B5a built for this situation working as designed.
Cropping is not a substitute: it recovers far less (26% vs. 91%) and does
so inconsistently across frames, because the mechanism it addresses (one
specific frame's board-outline nesting) is narrower than the mechanism
`List` addresses (nesting from any cause, including cards nesting under
each other).

This is a **change to a CLAUDE.md-pinned decision** ("Card detection":
"`findContours` `RETR_EXTERNAL`"), not something this package's write
scope authorizes unilaterally — CLAUDE.md's own default for
`ContourDetectorOptions.RetrievalMode` stays `External` in the code
committed here, per the brief. The user should decide whether to flip the
default, given this evidence, before any shipped-path change is made.

Two residual items for whoever does that follow-up:
1. **H2's small residual** (Zidane on `a_1`, two cards on `a_5`) is not
   fixed by `List` and would need its own investigation (lighting,
   border-color-specific contrast enhancement, or accepting the miss).
2. **The two high-distance wrong identifications** on `a_1` (Cat
   Warriors→Plains at 288, Defibrillating Current→Urza's Power Plant at
   272) are an identification, not a detection, question — both cards
   *were* detected as clean quads — and were not chased further here;
   they may be the same crop-scale phenomenon Part 2 of this document's
   B5c section already measured, at these specific grid positions.

## Decision: switched (2026-09-22)

**The user approved the switch.** `ContourDetectorOptions.RetrievalMode`'s
default is now `RetrievalModes.List`; `External` stays selectable on the
same option for anyone reproducing the old behaviour. The evidence is the
table above, restated here since it is now load-bearing rather than
advisory:

| Corpus | `RETR_EXTERNAL` | `RETR_LIST` |
|---|---|---|
| light mat, 15″ (a_corpus), full frame | 7/54 (13%) | 49/54 (91%) |
| light mat, 15″, cropped | 14/54 | 49/54 |
| light mat, 20″, full frame | 1/54 | 18/54 |
| dark mat, 15″ (2 frames) | 11/18 | 11/18 (identical) |

`List` was never worse in any of the 16 measured cells. Mechanism:
`External` returns only outermost contours, so on a light mat the bright
board forms a strong enclosing contour and adjacent cards merge into one
blob through the morphological close — the cards are discarded before any
filter runs, not rejected by one. On a dark mat the board forms no such
enclosure, which is exactly why `List` changes nothing there (11/18 both
ways) — mechanistic confirmation, not a lucky average. Measured cost: mean
5.0ms → 6.9ms per frame over 90 detections on the six real frames, ~2ms
against a 33ms/frame budget at 30fps.

### Re-running the existing real-capture checks under the new default

`Tests/StreamB/ContourCardDetectorRealCaptureTests.cs` asserts exactly one
accepted quad per `test-images/ad-hoc/` frame, with individually-named
exceptions. Re-run under `List`, two of the twelve frames changed, in
opposite directions — neither silently absorbed:

- **`verix_sleeved_black.png`** was a documented miss under `External`
  (Risk 1, sleeve glare fragmenting the contour). Under `List` it now
  detects correctly (263×370px, matching the card's known dimensions on
  the other two mats; confirmed against the annotated overlay, not just
  the count). Removed from the known-miss list; kept only as a historical
  note in the test file.
- **`solring_black.png`** now accepts a SECOND quad under `List`: the real
  card (265×371px) plus a 149×189px quad that the annotated overlay
  (`lab detect test-images/ad-hoc/solring_black.png`) places over a loop
  of desk cable in the frame's bottom-right corner — nowhere near the
  card. **This is not a `DedupeAndTakeTopN` bug**: `NestedDuplicate=3`
  fires correctly against the card's own inner frame/art-box contours;
  the cable-loop quad's centroid is not inside any accepted quad's
  polygon, so dedupe correctly leaves it alone. It happens to satisfy the
  aspect-ratio band (~1.27, inside ±15%) and the minimum-area floor by
  coincidence — a Risk-3 (false card detection) consequence of `List`
  surfacing far more raw candidate contours from a cluttered desk, not a
  dedupe defect and not fixable without tuning a filter against this one
  frame's clutter. Recorded as a documented known excess (expected count
  2, not 1) rather than chased.

`atarka_foil_black.png` (the other documented miss, foil glare) is
unaffected either way: still 0 accepted, identical rejection histogram.

### New finding: the real-corpus accuracy gate now fails

`Tests/StreamB/Accuracy/AccuracyHarnessRealCaptureTests.cs` runs the B6
accuracy harness against the same six `a_corpus` frames (via
`test-images/ground-truth.csv` + `test-images/fixtures/15in/9/`, which
*are* `a_1.png`…`a_6.png`) and gates on `wrong@1 == 0` at
`OkDistance=270` — zero tolerance, by design (`AccuracyHarnessOptions`:
"do not raise it to make a run pass").

Before the switch, this gate passed, because `External` only detected
1–2 of 9 cards per frame on this corpus — most slots never reached
identification at all (`DroppedFrame`: the detected count didn't match
the 9-card layout, so `SlotMapper` refused to guess an assignment).

After the switch, `List` detects 8–9 of 9 cards on most frames, so far
more slots actually reach identification — and one of them is a
confident wrong match: on `a_2.png`, slot 3 (ground truth "Young Red
Dragon // Bathe in Gold") rank-1 matches **"A-Young Red Dragon // A-Bathe
in Gold"** — the Alchemy-rebalanced printing of the same card, which
shares the same artwork — at distance 93 (well inside `OkDistance=270`)
with a margin of only 12 over the correct card. Full per-slot dump (via a
temporary diagnostic test, not committed):

```
a_2.png slot=3 truth=Young Red Dragon // Bathe in Gold outcome=Wrong
  rank1=A-Young Red Dragon // A-Bathe in Gold dist=93 margin=12 detectedCount=9
```

This is CLAUDE.md's already-accepted Risk 5 ("same-art printings are
permanently indistinguishable by hash") — not a detection defect, not a
`DedupeAndTakeTopN` defect, and not something in this package's write
scope (`src/LoreFetch.Core/Imaging/**`) to fix, since the hash/identifier
lives elsewhere. It was invisible before the switch purely because `a_2`
was one of the frames `External` mostly failed to detect (1/9), so this
slot never reached identification at all. The switch's higher detection
rate is what exposes it, on the only real 3×3 corpus currently available.

**Per this package's brief ("never delete or weaken a test to make the
switch pass... STOP and report"), this gate is left failing, and
`AccuracyHarnessOptions.MaxWrongAt1AtOkDistance` / `OkDistance` are left
unchanged.** `Tests/StreamB` therefore does not fully pass after this
switch: 1 failing test
(`AccuracyHarnessRealCaptureTests.Run_AgainstRealH3Corpus_ReportsAccuracyAndEvaluatesTheGate`),
253 passed, 1 skipped, 255 total under the CI filter
(`Category!=Hardware&Category!=WindowsOnly`), or 261/1/263 unfiltered.
This needs an explicit decision from whoever owns the accuracy gate: e.g.
accept the Alchemy-variant collision as a documented Risk-5 exception the
same way the detector test documents its known miss/excess, revisit once
the full H3 corpus (not just the six `a_corpus` frames) is available, or
address it in the identifier rather than the detector.

---

# Accuracy — B6 harness (correct@1 / wrong@1 / no-match)

**This section documents the HARNESS, not an accuracy result.** Package
B6's job was to build and debug a correct accuracy harness before the H3
fixture corpus exists — the corpus is being captured separately, and
nothing here should be read as a LoreFetch accuracy figure. The results
table at the end is a placeholder, explicitly labelled as such.

Production code: `src/LoreFetch.Lab/Accuracy/*` (`GroundTruthCsv`,
`GroundTruthFrame`/`GroundTruthOracleLookup`, `SlotMapper`,
`AccuracyFrameRunner`, `AccuracyStatistics`/`AccuracyGateResult`,
`AccuracyCorpusLoader`, `AccuracyReportFormatter`, `ThresholdsCalibration`)
and `src/LoreFetch.Lab/Synthetic/MultiCardFrameGenerator.cs`. CLI: `lab
accuracy [--index <path>] [--ok-distance N] [--max-wrong N]`. Tests:
`Tests/StreamB/Accuracy/*`.

## Definitions

Every ground-truth slot (`test-images/ground-truth.csv`, one row per card:
`file,height_in,layout,slot,oracle_name,rung,mat`) is classified into
exactly one of four outcomes, computed by `AccuracyFrameRunner.Run` from
the SAME `ICardDetector` → `IRectifier` → `ICardIdentifier` calls the
scanner itself makes:

| Outcome | Meaning |
|---|---|
| **Correct** | Rank-1's `OracleId` matches the ground truth. Distance is irrelevant to this classification — matching the RoundTrip gate's (B2) own definition. |
| **Wrong** | Rank-1's `OracleId` does NOT match, **and** its distance is ≤ `OkDistance` — a *confident* wrong answer. |
| **Unresolved** | Identification ran (the frame's detected count matched its layout) but returned no candidates, or a wrong rank-1 beyond `OkDistance` — an honest "don't know." |
| **DroppedFrame** | The frame's detected card count did not match its ground-truth layout, so this slot was never mapped or queried at all. |

**Reporting rule (orchestration-plan.md B6, "the three buckets sum to
100%"):** the headline table reports exactly three buckets —
**correct@1**, **wrong@1**, and **no-match** — and `Unresolved` +
`DroppedFrame` both fold into **no-match** for that sum (from the
collection's point of view, a slot the harness never identified is
indistinguishable from one it identified with no confidence — neither
ends up in the CSV). They stay two distinct `SlotOutcome` values
internally, and the full breakdown table still reports them separately
(`(Unres. Dropped)` columns), so a detection failure is never hidden
behind an identification failure. `AccuracyBucketCounts.AssertBucketsSumToTotal`
asserts the sum in code on every aggregation, not by eye.

**Headline scope:** non-land (`IsBasicLand`, resolved from the loaded
index — never the `rung` string) **and** `rung == "normal"`. Lands are
still classified and reported in the full per-height/per-rung breakdown
(a smoke test, per CLAUDE.md's Ladder), with the excluded count printed
next to the headline. `AccuracyHarnessSyntheticTests` deliberately tests
this with a land whose `rung` is mislabelled `"normal"`, specifically to
prove the exclusion is enforced by the flag and not by the label.

**Margin distribution:** rank-1 distance vs. the best DIFFERENT
`OracleId`'s distance, for every slot that returned ≥2 candidates
(`MaxCandidates` defaults to 3). `DroppedFrame` slots have no margin —
`Identify` was never called for them.

## Quad → slot mapping: row-BANDED, not a strict two-key sort

**Corrected mid-review against live evidence from a real captured frame,
before this package shipped.** The first implementation sorted quad
centroids by `OrderBy(Y).ThenBy(X)` — ascending Y, then ascending X. That
is wrong: hand-placed cards in one physical row never share exactly one Y
value (placement wobble, slight camera tilt), and a strict two-key sort
interleaves rows the moment one row's Y range gets close to its
neighbour's.

Confirmed on a real 3×3 frame's own detected centroids — the true top row
was `(593,92)`, `(839,99)`, `(1109,96)`, a 7px Y spread — which the naive
sort orders as `(593,92)`, `(1109,96)`, `(839,99)`: **slots 2 and 3
swapped**, on a frame where detection and identification both worked. A
synthetic grid with perfectly-aligned rows cannot catch this; it only
shows up when same-row members differ slightly in Y, which real frames
always do. `SlotMapperTests.SortRowMajor_RealCapturedTopRowWithYJitter_StillOrdersByXAscending`
pins these exact coordinates as a permanent regression test, and a
temporary chaos revert to the naive sort was confirmed to reproduce the
swap exactly (then reverted).

**The fix — `SlotMapper.SortRowMajor` — bands rows before ordering
within them:**
1. Compute each quad's centroid and a per-quad "height" (average of its
   two side-edge lengths).
2. Sort by centroid Y ascending.
3. Walk the sorted list, greedily grouping into bands: a quad joins the
   current band when its Y is within `tolerance` of that band's running
   average Y; otherwise it starts a new band. Because the input is
   already Y-sorted, this is a single linear pass.
4. Concatenate the bands (already top-to-bottom by construction) and sort
   each band's own members by centroid X ascending.

`tolerance` is **half the MEDIAN quad height of the frame's own detected
quads** — not a fixed pixel constant, because card pixel size scales with
camera height (~216×303px at 15in, ~165×235px at 20in per CLAUDE.md's
`px/inch = 1360/height_inches`; a fixed tolerance would be wrong at one
height or the other). Real row spacing is about one full card height, so
half a card height safely bands same-row jitter while staying clear of
the next row.

**Composes with the count-mismatch rule below without special-casing:** a
SHORT row (e.g. an 8-of-9 frame missing one card) still bands and orders
correctly on its own — `SortRowMajor` makes no assumption about row size,
so a short row never shifts another row's members into the wrong band.
`SlotMapperTests.SortRowMajor_ShortRowMissingOneMember_DoesNotShiftOtherRows`
pins this directly.

## Count mismatch: never paired by position

If `ICardDetector.Detect(frame, maxCards: frame.Layout)` returns a count
different from `frame.Layout`, `SlotMapper.TryMapToSlots` returns `false`
and no mapping at all — there is no code path that zips quads to slots by
index when the counts disagree. Every slot in that frame is then reported
as `DroppedFrame` by `AccuracyFrameRunner`, and `ICardIdentifier.Identify`
is never called for any of them (pinned by a scripted identifier that
throws if called with zero programmed responses left).

Because `Detect`'s own contract is "at most `maxCards`", and the runner
always asks for exactly `frame.Layout`, the detected count can never
exceed the layout through this call — the only reachable mismatch is
UNDER-detection, matching B5a's own real-capture evidence (a sleeved card
went undetected; nothing false-positived an extra card into an
already-full count). `SlotMapper.TryMapToSlots` still refuses an
over-count defensively as a general property of its own contract.

**This is the single highest-value test in the package**
(orchestration-plan.md H3 note), and it is run twice: once as pure
`SlotMapper`/`AccuracyFrameRunner` logic against stub quads (no image),
and once end-to-end through the real `ContourCardDetector` →
`PerspectiveRectifier` → `HashCardIdentifier` on a synthetic 3-card frame
whose ground truth claims a 4th, absent card
(`AccuracyHarnessSyntheticTests.Run_LayoutClaimsOneMoreCardThanIsPhysicallyPresent_DropsEveryHouseSlot_NoMisalignment`) —
asserting not just that every slot drops, but that none of the three
genuinely-present, genuinely-identifiable cards leak through as a
Correct/Wrong classification under the wrong slot.

## Coverage and partial-corpus labelling

H3 delivers the fixture corpus in batches. The harness runs on whatever
subset of `test-images/ground-truth.csv`'s frames has a file on disk
(`AccuracyCorpusLoader.SplitByPresence`) and every report — console, test
output, and (when this section's placeholder is filled in) this document —
opens with a coverage line naming exactly how many frames were found, and
which heights/rungs/mats they cover, so a partial run is never
presentable as the full result. Ground-truth rows naming a fixture not
yet on disk are skipped, not counted as failures.

## Thresholds: calibrated, but never written by this package

`ThresholdsCalibration.Suggest` computes a candidate `goodDistance`
(largest own-distance among headline `Correct` slots) and `okDistance`
(one less than the smallest rank-1 distance among headline `Wrong`
slots, falling back to `AccuracyHarnessOptions.OkDistance`'s own
documented prior — CardSpotter's upstream `myOkMatchScore` default, 270 —
when no wrong slot was observed). `ThresholdsCalibration.Write` merges
those into `data/index/thresholds.json` without disturbing B2's existing
fields. **Neither is called anywhere in this package against synthetic
data** — those two values must be calibrated from the REAL H3 corpus, and
writing them from procedural test cards would plant a fabricated
threshold that reads as a real measurement. `data/index/thresholds.json`
is unmodified by this package.

## Harness self-test (synthetic, in-memory index — NOT an accuracy result)

`AccuracyHarnessSyntheticTests` drives the full real pipeline
(`ContourCardDetector` → `PerspectiveRectifier` → `HashCardIdentifier`)
against `MultiCardFrameGenerator`-composited frames of procedural
"card-like" images (never a real Scryfall render or camera photo) and a
9-entry in-memory index. Full report from the 9-card-grid test (one
entry deliberately marked a basic land):

```
=== HARNESS SELF-TEST (synthetic, in-memory index) -- NOT a LoreFetch accuracy result ===
Full corpus -- 1 of 1 ground-truth frame(s) found on disk -- heights: 15in; rungs: normal; mats: light.

Options: OkDistance=270, MaxWrongAt1AtOkDistance=0, MaxCandidates=3

Headline (non-land, rung=normal):
correct@1=8 (100.0%)  wrong@1=0 (0.0%)  no-match=0 (0.0%) [unresolved=0, dropped-frame=0]  total=8

Lands excluded from the headline: 1

Full breakdown, per height x rung (lands and stretch cards included -- informational, not headline):
  Height Rung      Correct  Wrong  NoMatch  (Unres. Dropped)  Total  Correct%
    15in normal          9      0        0        0         0      9    100.0%

Margin distribution (rank-1 vs. best different OracleId), n=9: min=27, mean=93.0, median=105, max=139.

Gate: PASS -- wrong@1 = 0 (headline: non-land, normal-rung slots), within the bound of 0 at OkDistance=270.
```

## Interaction with the real-corpus detection investigation above

The retrieval-mode/mat-contrast investigation earlier in this document
found that on real 3×3 frames, the shipped `RETR_EXTERNAL` default
detects only 1–2 of 9 cards (nesting), while `RETR_LIST` recovers 8–9 of
9 at 15in but only ~3 of 9 at 20in — and the shipped default has not been
changed pending a user ruling. **When B6 eventually runs against the real
H3 corpus, a low correct@1 will very likely reflect that unresolved
detection question, not an identification failure** — most slots on an
under-detected frame become `DroppedFrame`, not `Wrong`, which is exactly
why this harness reports the two separately rather than folding detection
failures into the identification numbers.

## Results — PLACEHOLDER, pending the real H3 corpus

**Not yet run against real fixtures.** `test-images/ground-truth.csv` and
`test-images/fixtures/` do not exist in this worktree as of this writing
(`AccuracyHarnessRealCaptureTests` skips with that exact reason). When H3
delivers a batch, run `lab accuracy` (or let
`AccuracyHarnessRealCaptureTests` run un-skipped) and replace this
section with the real coverage line, the headline bucket counts, the full
breakdown table, the margin distribution, and the gate result — labelled
with the coverage they actually had, per the partial-corpus rule above.
