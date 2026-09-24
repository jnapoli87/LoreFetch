# Testing strategy

Spans every domain. Written for the v0.1 build, so its timeline talks about streams and "Stream 0" (the key is at the top of [`CONTRACTS.md`](CONTRACTS.md)); the five levels and what each must assert still stand. The governing constraint: **no card imagery may ever be committed** (WotC IP, regardless of who photographed it), so CI can never run against the real fixture corpus. That single fact shapes the whole strategy.

---

## When each test arrives — the gating model

The obvious trap: write the end-to-end test early and CI is red for twelve hours, which trains everyone to ignore CI. That is worse than having no CI. But deferring integration tests to "the integration checkpoint" reliably means writing them at hour 20 in a hurry.

Neither is necessary, because **the contracts are frozen**. Two mechanisms avoid the tension entirely:

### 1. The fakes make an end-to-end test possible on day zero

Stream 0 ships the scan pipeline and seven fakes (`FolderFrameSource`, `StubCardDetector`, `StubRectifier`, `StubCardIdentifier`, `StubOracleCatalog`, `StubCollectionStore`, `StubCollectionExporter`). So the full path — frame source → detect → rectify → identify → cohort → commit → CSV — is **testable and green from the end of Stream 0**, long before the hash or the camera exist.

It tests none of the hash's correctness. It tests the *wiring*: that the contracts compose, that frame ownership holds, that exclude/discard/clear semantics hold, that commit is idempotent, that the CSV shape is right. **That is where integration bugs actually live** — and across four worktrees, a test that fails the instant someone breaks a contract is the single highest-value guard available.

**Its frames are generated at test setup**, not committed: plain fills with a drawn rectangle, written to a temp folder that `FolderFrameSource` then reads. The stub detector ignores pixel content, so this exercises the real demo-path code with zero rasters in git. For the real-implementation cases, stream B's synthetic generator (B7) fills the same folder slot.

### 2. Real implementations reuse the same tests, gated by skip

Real implementations do **not** get new integration tests. They get the *same* tests with the real implementation injected. Write against the interface, inject the implementation, and gate on artifact presence:

```
if (!File.Exists(indexPath))
    Assert.Skip("hash index not built yet — stream B incomplete");
```

**Skipped is not red.** A skip that says *why* is honest signal, keeps CI green, and the skip count becomes a live progress indicator of what is not yet wired.

This makes the integration milestone mechanical rather than a judgement call:

> **Integration is done when `LOREFETCH_REQUIRE_REAL=1 dotnet test` reports zero skips on the local `win-x64` machine with every artifact present.**

Not "zero skips in CI", which is unreachable by construction: the real-implementation cases need the hash index, Scryfall renders and the fixture corpus, and **card imagery can never be committed**. CI skips are therefore expected and permanent, and each one must still state its reason. The environment switch is what makes the milestone mechanical — with `LOREFETCH_REQUIRE_REAL=1` set, every artifact-gated skip becomes a **failure**, so the machine that does have the artifacts cannot quietly pass with the real path untested.

Its corollary matters too: a skip that survives past its checkpoint is a bug, not a convenience. Flipping that switch at the integration checkpoint is what stops skips becoming permanent and the suite silently stopping testing anything real.

### What blocks a PR merging into `main`

| Must be green | Must not block |
|---|---|
| Build, both CI legs | Accuracy tables (measurement, not pass/fail) |
| All unit tests | Hardware verification (can't run in CI) |
| The fakes end-to-end test | Work in other open PRs |
| The tests of every domain the PR touches | Tests skipped for a missing artifact |
| The architecture rules (`Tests/Architecture`) | |
| `contract-check`, and the `metrics` ratchet (see *CI*) | |

Each domain has its own test project, and the end-to-end suite lives in `Tests/Integration/`, so a failure points at a domain rather than at "the tests". See the [domain map](CONTRACTS.md#domain-map).

## The five levels

| Level | What it covers | Where it lives | Live from | Runs in CI? |
|---|---|---|---|---|
| **Unit** | Pure logic: trigger state machine, tile state transitions, hash invariants, CSV store, quad filtering, escaping | The domain test projects (`Tests/App`, `Tests/Detection`, …), plus `Tests/Integration/Unit` for the pipeline | as written | ✅ |
| **Integration (fakes)** | Frame source → detect → rectify → identify → cohort → commit → CSV, all through stubs | Stream 0 | **end of Stream 0** | ✅ |
| **Integration (real)** | The *same* tests with real implementations injected | Stream 0, impls swapped in | skips until the artifact exists | ✅ (skips, never red) |
| **Accuracy** | Real fixture corpus: correct@1 / wrong@1 / no-match × height × difficulty | stream B, **local only** | when fixtures captured | ❌ images can't be committed |
| **Hardware** | Live C920: negotiated format, sustained memory, live accuracy | manual on the Windows PC | stream C | ❌ |

Note rows 2 and 3 are one test suite, not two. That is the whole point: parameterise over the implementation rather than duplicating the scenario.

Level 2 needs no card imagery at all (frames are generated at setup). Level 3 does, which is where the synthetic frame generator (stream B, task B7) comes in: it is not a convenience — it's the only committable substitute for real captures.

---

## What each level must actually assert

### Unit

**`IAutoCaptureTrigger`** is the highest-value unit target in the project: pure logic over a sequence of `(quads, expectedCount, now)` snapshots, no camera, no images, no clock of its own. Required cases:

- fires exactly once when the count matches and the scene is stable for ≥500 ms
- does **not** re-fire while the scene stays static ← *the re-arm rule; a static tableau must not fire every 500 ms forever*
- re-arms only after the scene breaks (count → 0, or movement beyond ε)
- never fires on a count mismatch
- `NotifyCaptured()` from the **manual** path suppresses an immediate auto-fire

**`CohortTile` transitions** (Stream 0) — `ToggleExcluded` round-trips from both `Included` and `ManuallySet` back to where it started; `SetManually` nulls `ChosenDistance`; `Clear` returns to `Included` or `Unresolved` according to the best candidate, and is a no-op unless `ManuallySet`.

**Hash invariants** (stream B) — assert **bounds, not invariants, and golden byte values as well**. Two input scales stay *within a bound* of each other rather than producing the same hash: step 3's downsample is lossy, so scale is not an invariance the hash has. Global brightness and gamma shifts stay within a bound. An inverted image lands at ≈1024. Then the per-cell upper order statistic and its tie-break get their own tests.

The golden hashes are the point, not a fallback: they are what catches an unintended change to either transform after the index is built, which is the failure mode that degrades matching silently instead of failing. They are generated and committed **on `win-x64`** and carry `[Trait("Category","WindowsOnly")]`, because `INTER_AREA` is not bit-exact across x86-64 and ARM64 — so the macOS leg filters them out rather than reporting a divergence it cannot avoid.

**CSV store** (stream D) — upsert increments quantity; `OracleId` + condition is the identity, with a blank condition treated as a value; blank condition is written as an empty field, never `null`; the atomic temp-then-rename leaves no partial file; a malformed line is reported rather than silently dropped.

### Integration (synthetic) — stream B

Generated frame at a known simulated height → the full query path → the expected oracle name. Includes **the round-trip gate**: a Scryfall render must retrieve **its own artwork** — asserted on `ArtworkId`, not `OracleId`, or the gate passes on a different art of the same card — **through the same code the scanner calls**, not a test-only shortcut.

The gate asserts a **bound, not equality**: the reference side blurs and downsamples where the query side does not, so a small distance floor is expected and demanding ≈ 0 would mean deleting the mechanism that makes the algorithm work. Record the measured floor and assert against it. Paired with **committed golden hashes, which run on the Windows leg only** — `INTER_AREA` is not bit-exact across x86-64 and ARM64, and `macos-latest` is ARM64, so a shared golden could only ever be red there. The index is built on `win-x64` and the goldens are pinned to the same architecture; the macOS leg filters them out by trait and keeps its real job, which is proving `Core` has no Windows-only dependency. Together these are what catch reference/query transform divergence, which otherwise degrades matching silently rather than failing.

### Integration (fakes, then real) — written in Stream 0, live from the end of Stream 0

One suite in `Tests/Integration/`, parameterised over the implementation set: `[fakes]` from the start, `[real]` skipping until each artifact exists. `FolderFrameSource` → detector → rectifier → identifier → `Cohort` → `CommitCohortAsync` → assert the CSV on disk. Must cover:

- an X'd tile is **absent** from the file
- Escape (discard) writes nothing at all
- a `ManuallySet` tile commits the corrected card with `Source = Manual` and no distance, not the machine guess
- a *Cleared* tile commits the machine's proposal again
- re-committing the same card increments quantity rather than adding a row
- a partial cohort (7 cards where 9 were expected) commits 7
- `CaptureAsync` rectifies the frame its latest snapshot came from, not a newer one — and is safe to call concurrently with the pipeline loop, which is the race that makes this test worth having
- every pooled `CameraFrame` is disposed exactly once over a sustained run
- **a tile's thumbnail is still readable after the cohort commits** ← a regression guard: the old `RectifiedCard` disposal footgun was removed by construction, and this keeps it removed

### Accuracy — local only

Reported as a committed **table**, not as pass/fail assertions — it's a measurement, and it's the spine of the demo. Two rules:

- ⚠️ **Normal cards only.** Every basic-land art collapses to one oracle name, so retrieving the wrong Forest still scores correct@1. Lands inflate the metric with a category that structurally cannot fail. Capture them, smoke-test with them, exclude them from the table — **enforced in code, not remembered.**
- **wrong@1 matters more than correct@1.** A silent miss is recoverable; a confident wrong answer is permanent bad inventory. Report both columns always.

### Hardware — manual

Negotiated 1080p30 MJPG proven from the device's own characteristics; flat memory over a sustained run; a deliberately slow consumer yields latency rather than unbounded growth; live accuracy within a stated tolerance of the corpus numbers.

---

## CI

`windows-latest` **and** `macos-latest`. The macOS leg is not about shipping macOS — it's what mechanically enforces that `Core` stays free of Windows-only dependencies. If it goes red because someone reached for a Windows API, that's the leg doing its job.

Both legs run Unit + Integration(synthetic) + Integration(end-to-end), and both filter out `Category=Hardware`. The macOS leg additionally filters out `Category=WindowsOnly`, which is how the golden hashes stay on one architecture. Neither runs Accuracy or Hardware.

A `lint` job checks formatting against `.editorconfig` (`dotnet format --verify-no-changes`; fix locally with `scripts/lorefetch.sh lint --fix`). Two more checks run on every PR, and each has a label that says "I meant it":

| Check | Fails when | Override label |
|---|---|---|
| `contract-check` | The PR touches the contract surface (`Core/Abstractions`, `Core/Scanning`, `Core/Fakes`), `Tests/Integration`, `Tests/Architecture`, or any build file | `contract-change` |
| `metrics` | Against `main`'s latest numbers: a test project has fewer tests; fewer tests *ran* (an existing test became a skip); or line coverage of `LoreFetch.Core`, `.Capture` or `.App` fell by more than 0.5 points | `tests-removed`, `coverage-drop` |

`metrics` is a ratchet, not a target: nothing may go backwards unannounced. It counts tests that *ran* rather than skips, because in CI every real-data test skips, so a new gated test adds a skip without taking anything away. `*PerformanceTests` classes are left out of the ran count, because their soft time budgets skip or run depending on the machine. Lab coverage is reported but never fails a PR. The numbers come from the Windows leg only (`scripts/lorefetch.sh test --results <dir>`, then `scripts/Metrics.cs`), and the table lands in the job summary. Adding or removing a label re-runs CI, which is how an override takes effect.

---

## Standing practice: chaos-test every regression test

When a test is added for a bug that was actually observed, **do not trust it because it passes against the fix.**

1. Temporarily re-apply the buggy code.
2. Run only the new test.
3. Confirm it fails — and read *why*. Trace the failure to the real defect, not to some incidental cause.
4. Revert to the real fix and confirm green.

A test that merely passes could be vacuous — mocked at a boundary that hides the bug, or asserting something trivially true. Reverting also surfaces the real blast radius: a swallowed exception can mean the production impact was *silently skipping work* rather than crashing, which isn't visible any other way.

Also check a new test doesn't just duplicate an existing one's coverage. If an existing test already covered the same path at the same fidelity, the bug would have been caught — and that discrepancy is itself worth investigating.

---

## Standing practice: pin every interpolation flag and border mode

Behavioural and invariant tests cannot see an interpolation flag or border mode — they only see whether the output is roughly right, and a wrong filter still produces a roughly-right image. This has bitten Stream B four times: swapping the hash's step-5 resize from `INTER_AREA` to `INTER_LINEAR` left all 29 invariant tests green (B1a) and was only caught by committed golden hashes (B1b, the reason goldens exist at all); swapping the synthetic generator's downscale filter left all 179 tests green (B7); swapping that same generator's keystone warp from `Linear` to `Nearest` left all 182 tests green (B7 again, found by chaos-testing a spot no brief had named). Every one of those reports — "I ran the chaos case and nothing failed" — is the desired signal, not noise to tidy away; it's what exposed the gap each time.

Two techniques, and picking the wrong one is the trap:

- **Golden hashes**, when the *pixel values themselves* must be stable across machines. These must be generated on `win-x64` and traited `[Trait("Category","WindowsOnly")]` — `INTER_AREA` is not bit-exact on ARM64, and the macOS CI leg is ARM64, so a shared golden cannot pass there.
- **A differential pin**, when only the *filter choice* is at risk: expose the flag, assert the default is the intended value, and assert a different value produces different pixels. This is architecture-independent — both sides of the comparison are computed on whichever machine runs the test — so it works on the Mac, where a golden cannot even be generated.

Every interpolation flag and border mode in an image pipeline needs one of these two, chosen deliberately rather than defaulted to whichever is easier to write.

---

## Things deliberately not tested automatically

- **UI rendering.** Headless Avalonia testing exists but is fiddly and slow to write. View-model logic is unit-tested; visual correctness is verified by running the app on the Mac. In a 24-hour budget this is the right trade.
- **Live camera.** Cannot run in CI. Manual, on the PC.
- **Real-card accuracy.** Blocked by the no-imagery constraint, permanently.

Each of these is a deliberate gap, not an oversight — and each is covered by a manual step in the relevant stream's done-when.
