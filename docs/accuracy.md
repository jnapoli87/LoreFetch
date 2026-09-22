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
