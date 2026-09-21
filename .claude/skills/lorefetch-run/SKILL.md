---
name: lorefetch-run
description: Pull, build, test or run the LoreFetch app, or report the local environment. Use whenever the task is to build the solution, run the test suite, launch the app, or check whether this machine can build it — instead of composing dotnet commands by hand. Also use when a build or test result needs reproducing on the other machine (the Mac builds and pushes; the Windows PC pulls and runs).
---

# Running LoreFetch

**Everything goes through [`scripts/lorefetch.sh`](../../../scripts/lorefetch.sh).** Do not compose `dotnet build` / `dotnet test` / `dotnet run` by hand: the script carries the platform-correct test filter and a guard that hand-written commands do not, and it is the same entry point on both machines.

```bash
scripts/lorefetch.sh doctor    # environment report; changes nothing
scripts/lorefetch.sh build     # restore + build Release
scripts/lorefetch.sh test      # build, then test with the right filter
scripts/lorefetch.sh run       # build, then launch the app
scripts/lorefetch.sh           # pull, build, test, run
```

`--no-pull`, `--debug`, `--verbose`, `--all-tests`, `--hardware`, `--filter <expr>`, and `-- <args>` to pass arguments to the app. `--help` lists them.

## What the script does that you would otherwise get wrong

- **The test filter matches CI, per platform.** Always `Category!=Hardware`; on macOS also `Category!=WindowsOnly`, because the golden hashes are generated on `win-x64` and `INTER_AREA` is not bit-exact on ARM64. Run `dotnet test` bare on the Mac and the goldens fail for a reason that is not a bug.
- **It fails when zero tests are discovered.** `dotnet test` prints *"No test is available"* and **exits 0** if no adapter is registered — measured on 2026-09-21 with `xunit.v3` present but `xunit.runner.visualstudio` missing: three tests, none run, green. The script treats "ran nothing" as a failure.
- **It refuses to pull over a dirty tree**, and pulls `--ff-only`.
- **It discovers the solution and the app project** rather than hardcoding paths, and says plainly when neither exists yet instead of emitting an MSBuild error.

## Reading the result

A non-zero exit is a real failure; the last 60 lines of output are printed for it. On success only the summary lines show — pass `-v` for the full log. Report what the script reported, including a zero-test failure, which is not a flake.

## Which machine

The orchestrator builds and tests on the **Mac** and pushes. Six things are win-x64 by nature and belong to the **Windows PC**: the index build (B4d), the golden hashes (B1b), the camera run (C4), stream A's done-when run (A10), `LOREFETCH_REQUIRE_REAL=1` (I6) and release (E1–E2). See *Platform switch* in [`docs/orchestration-plan.md`](../../../docs/orchestration-plan.md). Never claim a win-x64 result from a Mac run.
