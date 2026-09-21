# Testing strategy

Spans all three streams, so it belongs to the spine rather than to any one of them. The governing constraint: **no card imagery may ever be committed** (WotC IP, regardless of who photographed it), so CI can never run against the real fixture corpus. That single fact shapes the whole strategy.

---

## When each test arrives — the gating model

The obvious trap: write the end-to-end test early and CI is red for twelve hours, which trains everyone to ignore CI. That is worse than having no CI. But deferring integration tests to "the integration checkpoint" reliably means writing them at hour 20 in a hurry.

Neither is necessary, because **the contracts are frozen**. Two mechanisms avoid the tension entirely:

### 1. The fakes make an end-to-end test possible on day zero

Stream 0 ships `FolderFrameSource`, `StubCardDetector` and `StubCardIdentifier`. So the full path — frame source → detect → rectify → identify → cohort → commit → CSV — is **testable and green from H2**, long before the hash or the camera exist.

It tests none of the hash's correctness. It tests the *wiring*: that the contracts compose, that cohort lifecycle and disposal behave, that exclude/discard semantics hold, that commit is idempotent, that the CSV shape is right. **That is where integration bugs actually live** — and across four worktrees, a test that fails the instant someone breaks a contract is the single highest-value guard available.

It would, for instance, catch the `RectifiedCard`-lifetime footgun: commit a cohort, then read a tile's thumbnail.

### 2. Real implementations reuse the same tests, gated by skip

Real implementations do **not** get new integration tests. They get the *same* tests with the real implementation injected. Write against the interface, inject the implementation, and gate on artifact presence:

```
if (!File.Exists(indexPath))
    Assert.Skip("hash index not built yet — stream B incomplete");
```

**Skipped is not red.** A skip that says *why* is honest signal, keeps CI green, and the skip count becomes a live progress indicator of what is not yet wired.

This makes the integration milestone mechanical rather than a judgement call:

> **Integration is done when the skip count reaches zero.**

Its corollary matters too: a skip that survives past its checkpoint is a bug, not a convenience. At the integration checkpoint, flip the gate so a skipped test **fails** — otherwise skips quietly become permanent and the suite silently stops testing anything real.

### What blocks a stream merging into `main`

| Must be green | Must not block |
|---|---|
| Build, both CI legs | Accuracy tables (measurement, not pass/fail) |
| All unit tests | Hardware verification (can't run in CI) |
| The fakes end-to-end test | Other streams' unfinished work |
| That stream's own tests | Tests skipped for a missing artifact |

Streams own disjoint directories, so their tests can't interfere with each other — which is what makes "green at every merge" achievable with four concurrent streams.

## The five levels

| Level | What it covers | Where it lives | Live from | Runs in CI? |
|---|---|---|---|---|
| **Unit** | Pure logic: trigger state machine, hash invariants, CSV store, quad filtering, escaping | each stream | as written | ✅ |
| **Integration (fakes)** | Frame source → detect → rectify → identify → cohort → commit → CSV, all through stubs | Stream 0 | **H2** | ✅ |
| **Integration (real)** | The *same* tests with real implementations injected | Stream 0, impls swapped in | skips until the artifact exists | ✅ (skips, never red) |
| **Accuracy** | Real fixture corpus: correct@1 / wrong@1 / no-match × height × difficulty | stream B, **local only** | when fixtures captured | ❌ images can't be committed |
| **Hardware** | Live C920: negotiated format, sustained memory, live accuracy | manual on the Windows PC | stream C | ❌ |

Note rows 2 and 3 are one test suite, not two. That is the whole point: parameterise over the implementation rather than duplicating the scenario.

The synthetic frame generator (stream B, task B7) is what makes levels 2 and 3 possible at all. It is not a convenience — it's the only committable substitute for real captures.

---

## What each level must actually assert

### Unit

**`IAutoCaptureTrigger`** is the highest-value unit target in the project: pure logic over a sequence of `(quads, expectedCount, now)` snapshots, no camera, no images, no clock of its own. Required cases:

- fires exactly once when the count matches and the scene is stable for ≥500 ms
- does **not** re-fire while the scene stays static ← *the re-arm rule; a static tableau must not fire every 500 ms forever*
- re-arms only after the scene breaks (count → 0, or movement beyond ε)
- never fires on a count mismatch
- `NotifyCaptured()` from the **manual** path suppresses an immediate auto-fire

**Hash invariants** (stream B) — assert properties, not golden byte values: scale invariance, stability under global brightness/gamma shift, and that an inverted image does *not* match.

**CSV store** — upsert increments quantity; oracle-name + condition is the identity; the atomic temp-then-rename leaves no partial file; a malformed line is reported rather than silently dropped.

### Integration (synthetic) — stream B

Generated frame at a known simulated height → the full query path → the expected oracle name. Includes **the round-trip gate**: a Scryfall render must retrieve *itself* at Hamming distance ≈ 0 **through the same code the scanner calls**, not a test-only shortcut. This is the test that catches reference/query transform divergence, which otherwise degrades matching silently rather than failing.

### Integration (fakes, then real) — written in Stream 0, live from H2

One suite, parameterised over the implementation set: `[fakes]` from H2, `[real]` skipping until each artifact exists. `FolderFrameSource` → detector → rectifier → identifier → `Cohort` → `CommitCohortAsync` → assert the CSV on disk. Must cover:

- an X'd tile is **absent** from the file
- Escape (discard) writes nothing at all
- a `ManuallySet` tile commits the corrected name, not the machine guess
- re-committing the same card increments quantity rather than adding a row
- a partial cohort (7 cards where 9 were expected) commits 7
- **a tile's thumbnail is still readable after the cohort commits** ← catches the `RectifiedCard` disposal footgun flagged in `CONTRACTS.md`, which otherwise surfaces as corrupt images during a demo

### Accuracy — local only

Reported as a committed **table**, not as pass/fail assertions — it's a measurement, and it's the spine of the demo. Two rules:

- ⚠️ **Normal cards only.** Every basic-land art collapses to one oracle name, so retrieving the wrong Forest still scores correct@1. Lands inflate the metric with a category that structurally cannot fail. Capture them, smoke-test with them, exclude them from the table — **enforced in code, not remembered.**
- **wrong@1 matters more than correct@1.** A silent miss is recoverable; a confident wrong answer is permanent bad inventory. Report both columns always.

### Hardware — manual

Negotiated 1080p30 MJPG proven from the device's own characteristics; flat memory over a sustained run; a deliberately slow consumer yields latency rather than unbounded growth; live accuracy within a stated tolerance of the corpus numbers.

---

## CI

`windows-latest` **and** `macos-latest`. The macOS leg is not about shipping macOS — it's what mechanically enforces that `Core` stays free of Windows-only dependencies. If it goes red because someone reached for a Windows API, that's the leg doing its job.

Both legs run Unit + Integration(synthetic) + Integration(end-to-end). Neither runs Accuracy or Hardware.

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

## Things deliberately not tested automatically

- **UI rendering.** Headless Avalonia testing exists but is fiddly and slow to write. View-model logic is unit-tested; visual correctness is verified by running the app on the Mac. In a 24-hour budget this is the right trade.
- **Live camera.** Cannot run in CI. Manual, on the PC.
- **Real-card accuracy.** Blocked by the no-imagery constraint, permanently.

Each of these is a deliberate gap, not an oversight — and each is covered by a manual step in the relevant stream's done-when.
