# Stream reviews — directions

Step 2 of the review sequence in [`PLAN.md`](PLAN.md#review-sequence--before-any-code): four plan reviewers, one per stream, run in parallel **before any code exists**. The architecture review has already settled boundaries, ownership and the contract surface; each reviewer now goes deep on one stream **without the others' context**.

Isolation is the point. A reviewer who has read the whole plan inherits its assumptions; a reviewer who sees only its own stream and the seam asks whether *this* stream actually works.

| Stream | Doc | Branch |
|---|---|---|
| A — UI | [`stream-a-ui.md`](stream-a-ui.md) | `review/stream-a` |
| B — Identification | [`stream-b-identification.md`](stream-b-identification.md) | `review/stream-b` |
| C — Capture | [`stream-c-capture.md`](stream-c-capture.md) | `review/stream-c` |
| D — Collection & export | [`stream-d-export.md`](stream-d-export.md) | `review/stream-d` |

---

## For the orchestrating session

You launch the four reviewers, check what they hand back, and report to the user. **You do not review, merge or reconcile** — reconciliation is step 3, a separate full-context pass.

### Before launching

1. Confirm `main` contains the architecture review: `CONTRACTS.md` has a *"Resolved in the architecture review"* section and `CONTEXT.md` exists at the repo root. If not, stop and tell the user.
2. Confirm `jq` is on `PATH` (`jq --version`). Without it `.claude/hooks/guard-write.sh` fails open and the frozen-surface guard silently does nothing.

### Launching

3. Create one worktree per stream, each on its own branch from `main` (table above).
4. Start one Claude session in each worktree with **exactly** the launch prompt below, with `<X>` and `<doc>` filled in. **Nothing else** — not your own summary, not the plan, not another reviewer's findings. Anything you add is the cross-stream context this whole step exists to keep out.

### While they run

5. **Relay nothing between reviewers.** If one asks a question that depends on another stream, answer: *"Out of scope — record it under Open questions."* If one asks the user a question, pass it to the user verbatim and pass the answer back verbatim.
6. If a reviewer wants to edit a file other than its own stream doc, the answer is no: it records the change under *Proposed contract changes* or *Open questions* instead.

### When each finishes

7. Check its branch:
   - `git diff --stat main...review/stream-<x>` shows **exactly one file** — its own stream doc. Anything else: tell the reviewer to revert it.
   - The doc ends with a *"Plan review findings"* section containing the four subsections below (empty ones say "None").
   - At least one commit, authored as `jnapoli87 <jnapoli87@users.noreply.github.com>`.
   - **Nothing pushed.** Pushing is the user's call.
8. When all four are in, report to the user, per stream: branch, commit, and a count of *Corrected*, *Proposed contract changes* and *Open questions* — plus the full text of every **Proposed contract change** and **Open question**, verbatim, since those are what the user and the reconciliation pass act on. Don't summarise or rank them; you haven't reviewed them.

### Don't

- Don't merge any review branch, push, or edit any doc yourself.
- Don't resolve conflicts between reviewers' proposals — flag them to the user as conflicts. That's reconciliation's job.

---

## Launch prompt

Fill in `<X>` and `<doc>` from the table. Use it verbatim.

```text
You are the Stream <X> plan reviewer for LoreFetch.

Read docs/stream-review-directions.md, section "For each stream reviewer", and follow it exactly. Your stream doc is docs/<doc>.
```

---

## For each stream reviewer

You review **one stream's plan**, before any code exists. Your job is to find what's wrong with it now, while fixing it costs an edit instead of a rewrite.

### Read

- Your stream doc — the one named in your launch prompt.
- [`CONTRACTS.md`](CONTRACTS.md) — the seam your stream builds against.
- [`../CONTEXT.md`](../CONTEXT.md) — the glossary. Use its terms; avoid the ones it lists under *Avoid*.
- `CLAUDE.md` loads automatically. Its settled decisions bind you.

**Don't read** `PLAN.md`, `TESTING.md`, other streams' docs, other review branches, or this repo's git history. They're not secret — they're out of scope, and reading them brings in exactly the cross-stream assumptions this review is meant to avoid.

### Do

1. **Work through your doc's *"Plan review: research targets"* section** against primary sources: official docs, source code, release notes, the tool's own import documentation. Not blog posts or tutorials. Cite a URL for every finding.
2. **Check every other factual claim in your doc** — versions, API names, numbers, behaviours. A claim you can't verify is a finding too: say so.
3. **Seam check.** For each task, can it be done using only what your doc's *Consumes* line lists? If not, that's a proposed contract change.
4. **Gaps.** Missing tasks, unowned risks, *done-when* criteria that can't actually be tested, fallbacks that don't fall back to anything.

### Write — your stream doc only

- **Correct errors inline**, where they are, each with its source.
- **Append** this section at the end of the doc:

```markdown
## Plan review findings — <date>

### Verified
- <claim> — <source URL>

### Corrected
- <what the doc said> → <what's true> — <source URL>

### Proposed contract changes
- **<type or member>**: <the change>. **Why:** <what in this stream needs it>.
  **Effect on other streams:** unknown — for reconciliation.

### Open questions
- <a decision for the user, with your recommendation and why>
```

Empty subsections say "None".

### Don't

- **Don't edit any other file** — not `CONTRACTS.md`, not `CONTEXT.md`, not `CLAUDE.md`. A contract change goes under *Proposed contract changes*; it gets applied once, in reconciliation, against all four streams' proposals together.
- **Don't write code.** This is a plan review.
- **Don't re-propose anything in `CLAUDE.md`'s *"Rejected — do not re-propose"* table.** If you find the *reason* for a rejection is factually wrong, that's an open question with a source, not a reversal.
- **Don't push.** Commit on your branch and stop.

### Finish

Make one commit on your branch — `Stream <X> plan review: <one-line summary>`. The repo's git config supplies the right identity; if the pre-commit hook refuses the commit, stop and report it — don't work around it. Then reply with a short summary: what you verified, what you corrected, and how many contract changes and open questions you raised.
