# Contributing

## Issue → branch → PR

1. **Open an issue first.** Bugs use the *Bug report* form. Note the number, because everything after it refers to it.
2. **Branch from `main`:** `fix/<issue>-<slug>` or `feat/<issue>-<slug>`, e.g. `fix/3-flaky-folderframesource-test`.
3. **Commit** with messages that say why, not just what. A bug fix carries a regression test in the test project of the domain it touches ([domain map](docs/CONTRACTS.md#domain-map)), **chaos-tested**: re-apply the bug, watch the test fail for the right reason, revert.
4. **Open a PR** whose body starts `Fixes #<issue>`, so merging closes the issue.
5. **CI must be green:** both build legs, `lint`, `guards`, `contract-check`, and `metrics`. For a `lint` failure, `scripts/lorefetch.sh lint --fix` applies the formatting. Their override labels (`contract-change`, `tests-removed`, `coverage-drop`) are for deliberate changes; say why in the PR. See [`docs/TESTING.md`](docs/TESTING.md#ci).
6. **Squash-merge.** The branch is deleted automatically.

`main` accepts changes only through PRs.

## Writing issues and PRs

Keep both **terse**: facts, not narrative.

- **Issue:** the symptom and the evidence (failing assertion, file:line, where it was seen), plus the suspected cause if known.
- **PR:** `Fixes #<issue>`, then what changed as a short list, then how it was verified.

The reasoning goes in commit messages and code comments, where it stays with the code.

## Build and test

`scripts/lorefetch.sh test` builds and runs the suite exactly as CI does, and `scripts/lorefetch.sh lint` checks formatting (`--help` lists the options). Formatting rules live in `.editorconfig`. Tests that need real data skip without it; see [`Tests/Support/README.md`](Tests/Support/README.md).

## Releasing

The version lives in one place, the tag. CI builds the zip from it (`.github/workflows/release.yml`), so no local build ever ships.

1. **Notes PR.** Add `docs/release-notes/vX.Y.Z.md`: what changed for someone using the app, and anything they must do differently. Merge it like any other PR. The release workflow refuses a tag that has no notes file.
2. **Tag `main` and push the tag:**
   ```bash
   git fetch origin && git tag -a vX.Y.Z origin/main -m "LoreFetch vX.Y.Z" && git push origin vX.Y.Z
   ```
3. **Smoke-test the draft.** The workflow tests the tagged commit, builds the zip and opens a **draft** release with the notes as its body. Download the zip from the draft, unzip it into a fresh folder, and scan a card.
4. **Publish** the draft. A broken draft never goes public: delete the draft and the tag (`git push origin :vX.Y.Z`), fix through a PR, and tag again.

Versions are `0.MINOR.PATCH`: a minor for new behaviour, a patch for fixes only. *Actions → Release → Run workflow* builds the same zip from any branch as a workflow artifact, with no release.

## Never commit card imagery

Scryfall renders and your own photos of cards are Wizards of the Coast IP. Keep them in the gitignored `test-images/`. The pre-commit hook rejects images outside the UI and doc asset folders, and the `guards` CI check runs the same hook. Why: [`docs/DECISIONS.md`](docs/DECISIONS.md#card-imagery-is-enforced-in-three-layers-not-just-documented).
