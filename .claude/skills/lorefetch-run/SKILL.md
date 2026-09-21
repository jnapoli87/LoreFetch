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

## How to invoke it from the Bash tool — read this before the first call

**`run` and the bare default never return on their own.** They end by launching an Avalonia window, and the process lives until someone closes it. In the foreground that blocks the tool call, times out or gets killed, and reports nothing about the build or the tests that already ran.

So split the work into two calls:

1. **Foreground, everything that terminates:** `scripts/lorefetch.sh test` (add `pull` first if a pull is wanted: `scripts/lorefetch.sh pull && scripts/lorefetch.sh test`). Read the result, and stop there if it is red.
2. **Background, the app only:** `scripts/lorefetch.sh run --no-pull` with `run_in_background: true`. The build is incremental, so this is quick. You are notified when the window closes. Don't sleep-poll it.

**Never pipe the script through `| tail`, `| head` or `| grep`.** The script already trims its own output: summary lines on success, the last 60 lines on failure. A pipe buffers everything until exit, so a long or blocking run shows nothing. It also replaces the script's exit code with the pipe's, so a failure reads as success.

**On Windows, close any running app before you build.** A running `LoreFetch.App.exe` is locked, so the next build fails with `MSB3027`/`MSB3021`, *"The file is locked by: LoreFetch.App (…)"*. That is not a code failure. Stopping a background task or killing a blocked call does **not** close the window it opened: the app outlives the shell that started it. So check `Get-Process LoreFetch.App` first. If an instance you launched is still running, stop it (`Stop-Process -Name LoreFetch.App`). If you didn't launch it, the user may still be using it, so ask before you close it.

Never run the bare `scripts/lorefetch.sh` from the tool. It is the entry point for a human at a terminal, who is the one who closes the window.

## What the script does that you would otherwise get wrong

- **The test filter matches CI, per platform.** Always `Category!=Hardware`; on macOS also `Category!=WindowsOnly`, because the golden hashes are generated on `win-x64` and `INTER_AREA` is not bit-exact on ARM64. Run `dotnet test` bare on the Mac and the goldens fail for a reason that is not a bug.
- **It fails when zero tests are discovered.** `dotnet test` prints *"No test is available"* and **exits 0** if no adapter is registered — measured on 2026-09-21 with `xunit.v3` present but `xunit.runner.visualstudio` missing: three tests, none run, green. The script treats "ran nothing" as a failure.
- **It refuses to pull over a dirty tree**, and pulls `--ff-only`.
- **It discovers the solution and the app project** rather than hardcoding paths, and says plainly when neither exists yet instead of emitting an MSBuild error.

## Reading the result

A non-zero exit is a real failure; the last 60 lines of output are printed for it. On success only the summary lines show — pass `-v` for the full log. Report what the script reported, including a zero-test failure, which is not a flake.

## Which machine

The orchestrator builds and tests on the **Mac** and pushes. Six things are win-x64 by nature and belong to the **Windows PC**: the index build (B4d), the golden hashes (B1b), the camera run (C4), stream A's done-when run (A10), `LOREFETCH_REQUIRE_REAL=1` (I6) and release (E1–E2). See *Platform switch* in [`docs/orchestration-plan.md`](../../../docs/orchestration-plan.md). Never claim a win-x64 result from a Mac run.
