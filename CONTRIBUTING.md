# Contributing

## Issue → branch → PR

1. **Open an issue first.** Bugs use the *Bug report* form. Note the number, because everything after it refers to it.
2. **Branch from `main`:** `fix/<issue>-<slug>` or `feat/<issue>-<slug>`, e.g. `fix/3-flaky-folderframesource-test`.
3. **Commit** with messages that say why, not just what. A bug fix carries a regression test in the test project of the domain it touches ([domain map](docs/CONTRACTS.md#domain-map)), **chaos-tested**: re-apply the bug, watch the test fail for the right reason, revert.
4. **Open a PR** whose body starts `Fixes #<issue>`, so merging closes the issue.
5. **CI must be green:** both build legs, `contract-check`, and `metrics`. Their override labels (`contract-change`, `tests-removed`, `coverage-drop`) are for deliberate changes; say why in the PR. See [`docs/TESTING.md`](docs/TESTING.md#ci).
6. **Squash-merge.** The branch is deleted automatically.

`main` accepts changes only through PRs.

## Writing issues and PRs

Keep both **terse**: facts, not narrative.

- **Issue:** the symptom and the evidence (failing assertion, file:line, where it was seen), plus the suspected cause if known.
- **PR:** `Fixes #<issue>`, then what changed as a short list, then how it was verified.

The reasoning goes in commit messages and code comments, where it stays with the code.

## Build and test

`scripts/lorefetch.sh test` builds and runs the suite exactly as CI does (`--help` lists the options). Tests that need real data skip without it; see [`Tests/Support/README.md`](Tests/Support/README.md).

## Never commit card imagery

Scryfall renders and your own photos of cards are Wizards of the Coast IP. Keep them in the gitignored `test-images/`. The pre-commit hook rejects staged images outside the UI and doc asset folders. Why: [`docs/DECISIONS.md`](docs/DECISIONS.md#card-imagery-is-enforced-in-two-layers-not-just-documented).
