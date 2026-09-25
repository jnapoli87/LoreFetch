#!/bin/sh
#
# LoreFetch — pull, build, test, run.
#
# One entry point on every machine and in CI, so a local run and a CI run
# apply the same filter and the same guards.
#
#   scripts/lorefetch.sh              # pull, build, test, then run the app
#   scripts/lorefetch.sh setup        # wire the pre-commit hook (do this first
#                                     # in any fresh clone — see doctor)
#   scripts/lorefetch.sh doctor       # environment report + hook verdict; fails
#                                     # if the hook is not wired
#   scripts/lorefetch.sh pull
#   scripts/lorefetch.sh build
#   scripts/lorefetch.sh test
#   scripts/lorefetch.sh lint         # formatting check, as CI runs it
#   scripts/lorefetch.sh lint --fix   # apply the formatting instead
#   scripts/lorefetch.sh run -- --demo   # everything after -- goes to the app
#
# Options
#   --no-pull          skip the pull (default for build/test/run when named
#                      explicitly; `all` pulls unless you pass this)
#   --debug            Debug instead of Release
#   --filter <expr>    override the test filter entirely
#   --hardware         include Category=Hardware tests (needs the C920)
#   --all-tests        no category filter at all
#   --fix              lint: apply formatting instead of checking it
#   --results <dir>    also write TRX results and code coverage to <dir>
#                      (what scripts/Metrics.cs reads; CI's Windows leg)
#   -v, --verbose      show full dotnet output instead of the tail
#
# POSIX sh on purpose: on Windows this runs under Git Bash, which is already
# required by hooks/pre-commit. Do not reach for bash builtins here.

set -eu

# --------------------------------------------------------------------------
# Locate the repo from the script's own path, so it works from any cwd and
# from inside a linked worktree.
# --------------------------------------------------------------------------
script_dir=$(cd "$(dirname "$0")" && pwd -P)
REPO=$(cd "$script_dir/.." && pwd -P)
cd "$REPO"

CONFIG=Release
DO_PULL=auto
VERBOSE=0
FILTER=""
INCLUDE_HARDWARE=0
NO_FILTER=0
RESULTS_DIR=""
FIX=0
CMD=""
APP_ARGS=""

die()  { printf 'lorefetch: %s\n' "$1" >&2; exit 1; }
say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

# --------------------------------------------------------------------------
# Arguments
# --------------------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    all|pull|build|test|lint|run|doctor|setup)
      [ -n "$CMD" ] && die "two commands given: $CMD and $1"
      CMD=$1 ;;
    --no-pull)     DO_PULL=no ;;
    --pull)        DO_PULL=yes ;;
    --debug)       CONFIG=Debug ;;
    --release)     CONFIG=Release ;;
    --hardware)    INCLUDE_HARDWARE=1 ;;
    --all-tests)   NO_FILTER=1 ;;
    -v|--verbose)  VERBOSE=1 ;;
    --filter)      shift; [ $# -gt 0 ] || die "--filter needs a value"; FILTER=$1 ;;
    --fix)         FIX=1 ;;
    --results)     shift; [ $# -gt 0 ] || die "--results needs a directory"; RESULTS_DIR=$1 ;;
    --)            shift; APP_ARGS="$*"; break ;;
    -h|--help)     sed -n '3,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             die "unknown argument: $1  (try --help)" ;;
  esac
  shift
done

[ -n "$CMD" ] || CMD=all
case "$CMD" in
  all)                        [ "$DO_PULL" = auto ] && DO_PULL=yes ;;
  build|test|lint|run|doctor|setup) [ "$DO_PULL" = auto ] && DO_PULL=no ;;
esac

# --------------------------------------------------------------------------
# Platform and the default test filter
# --------------------------------------------------------------------------
# Mirrors .github/workflows/ci.yml deliberately: a local run that passes where
# CI fails, or vice versa, is worse than no local run. Both legs drop Hardware;
# the macOS leg also drops WindowsOnly, because the golden hashes are generated
# on win-x64 and INTER_AREA is not bit-exact on ARM64 (DECISIONS.md, Risk 2).
uname_s=$(uname -s 2>/dev/null || echo unknown)
case "$uname_s" in
  Darwin)            PLATFORM=macos ;;
  MINGW*|MSYS*|CYGWIN*) PLATFORM=windows ;;
  Linux)             PLATFORM=linux ;;
  *)                 PLATFORM=$uname_s ;;
esac

default_filter() {
  if [ "$NO_FILTER" -eq 1 ]; then printf ''; return; fi
  f=""
  [ "$INCLUDE_HARDWARE" -eq 1 ] || f="Category!=Hardware"
  if [ "$PLATFORM" = macos ]; then
    [ -n "$f" ] && f="$f&Category!=WindowsOnly" || f="Category!=WindowsOnly"
  fi
  printf '%s' "$f"
}

# --------------------------------------------------------------------------
# Discover the solution and the app project.
#
# Discovered rather than hardcoded, so a checkout without them gets a plain
# message instead of a confusing MSBuild error.
# --------------------------------------------------------------------------
find_solution() {
  for f in "$REPO"/*.slnx "$REPO"/*.sln; do
    [ -f "$f" ] && { printf '%s' "$f"; return 0; }
  done
  return 1
}

find_app() {
  if [ -f "$REPO/src/LoreFetch.App/LoreFetch.App.csproj" ]; then
    printf '%s' "$REPO/src/LoreFetch.App/LoreFetch.App.csproj"; return 0
  fi
  # Fall back to any project that produces an executable.
  for f in "$REPO"/src/*/*.csproj; do
    [ -f "$f" ] || continue
    if grep -qi '<OutputType>\(Win\)\{0,1\}Exe</OutputType>' "$f"; then
      printf '%s' "$f"; return 0
    fi
  done
  return 1
}

run_dotnet() {  # $1 = label, rest = args
  label=$1; shift
  say "$label"
  note "dotnet $*"
  if [ "$VERBOSE" -eq 1 ]; then
    dotnet "$@"
  else
    log=$(mktemp "${TMPDIR:-/tmp}/lorefetch.XXXXXX")
    if dotnet "$@" > "$log" 2>&1; then
      tail -5 "$log"; rm -f "$log"
    else
      status=$?
      printf '\n--- dotnet failed (exit %s); last 60 lines ---\n' "$status"
      tail -60 "$log"; rm -f "$log"
      exit "$status"
    fi
  fi
}

# --------------------------------------------------------------------------
# doctor
# --------------------------------------------------------------------------
# `hooks/pre-commit` is tracked, so the FILE survives a fresh clone, but
# `core.hooksPath` is config, and config does not clone. Until setup runs, the
# hook silently never fires. CI runs the same checks as the `guards` job, so an
# unwired hook can no longer let imagery reach main; wiring it just moves the
# failure to before the push instead of after.
hooks_wired() {
  [ "$(git -C "$REPO" config core.hooksPath 2>/dev/null || true)" = "hooks" ]
}

cmd_setup() {
  say "Setup — repo-local config only, never global"
  git -C "$REPO" config --local core.hooksPath hooks
  chmod +x "$REPO/hooks/pre-commit" 2>/dev/null || true
  note "core.hooksPath  = hooks  (so the TRACKED pre-commit hook actually runs)"
  note ""
  note "Commit identity is yours to set; setup leaves it alone. Commits publish"
  note "whatever email they carry, so consider your GitHub noreply address:"
  note "  git config user.email <id>+<username>@users.noreply.github.com"
  note "author: $(git -C "$REPO" var GIT_AUTHOR_IDENT 2>/dev/null | sed 's/>.*/>/' || echo '?')"

  if ! hooks_wired; then
    note "STILL MISCONFIGURED after setup — run 'doctor' and read the output."
    exit 1
  fi
  note "Hook is wired. 'doctor' will confirm."
}

cmd_doctor() {
  say "Environment"
  note "platform     : $PLATFORM ($(uname -sm 2>/dev/null || echo '?'))"
  note "repo         : $REPO"
  note "branch       : $(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
  note "head         : $(git -C "$REPO" log -1 --format='%h %s' | cut -c1-72)"
  note "config       : $CONFIG"
  note "test filter  : $(default_filter | sed 's/^$/(none)/')"

  say ".NET SDKs"
  if command -v dotnet >/dev/null 2>&1; then
    dotnet --list-sdks | sed 's/^/    /'
    if dotnet --list-sdks | grep -q '^10\.'; then
      note "OK: a 10.0.x SDK is present."
    else
      note "MISSING: no 10.0.x SDK. global.json pins 10.0.x, so nothing will build."
    fi
    [ -f "$REPO/global.json" ] && note "global.json  : $(tr -d ' \n' < "$REPO/global.json")"
  else
    note "MISSING: dotnet is not on PATH."
  fi

  say "Git identity and hook wiring"
  note "author   : $(git -C "$REPO" var GIT_AUTHOR_IDENT 2>/dev/null || echo '?')"
  note "committer: $(git -C "$REPO" var GIT_COMMITTER_IDENT 2>/dev/null || echo '?')"
  note "hooksPath: $(git -C "$REPO" config core.hooksPath 2>/dev/null || echo '(UNSET — the tracked pre-commit hook does NOT run)')"

  say "Solution"
  if sln=$(find_solution); then note "solution : $sln"; else
    note "solution : none found"; fi
  if app=$(find_app); then note "app      : $app"; else
    note "app      : none yet"; fi

  # A verdict, not just a report: an unwired hook is silent, so doctor FAILS
  # on it rather than mentioning it among a dozen lines nobody reads.
  if ! hooks_wired; then
    printf '\n\033[31m%s\033[0m\n' "==> THE PRE-COMMIT HOOK IS NOT WIRED IN THIS CHECKOUT"
    note "Run:  scripts/lorefetch.sh setup"
    note ""
    note "hooks/pre-commit is tracked so the FILE arrives, but core.hooksPath is"
    note "config and config is never cloned. CI's 'guards' check still catches"
    note "imagery and attribution problems, but only after you push."
    exit 1
  fi
  printf '\n\033[32m%s\033[0m\n' "==> Hook is wired: hooks/pre-commit runs on every commit."
}

# --------------------------------------------------------------------------
# pull / build / test / run
# --------------------------------------------------------------------------
cmd_pull() {
  say "Pull"
  branch=$(git -C "$REPO" rev-parse --abbrev-ref HEAD)
  if [ -n "$(git -C "$REPO" status --porcelain)" ]; then
    git -C "$REPO" status --short | sed 's/^/    /'
    die "the working tree is dirty; commit or set the changes aside before pulling"
  fi
  note "branch $branch, fast-forward only"
  git -C "$REPO" pull --ff-only
  note "now at $(git -C "$REPO" log -1 --format='%h %s' | cut -c1-72)"
}

require_solution() {
  if ! SLN=$(find_solution); then
    say "Nothing to build yet"
    note "No .slnx or .sln in $REPO."
    note "This checkout has no solution file, so there is nothing to build."
    note "'scripts/lorefetch.sh doctor' still works."
    exit 0
  fi
}

# Formatting, per .editorconfig. `dotnet format` covers whitespace, code style
# and analyzer rules; --verify-no-changes makes it a check that fails instead
# of editing, which is what CI runs.
cmd_lint() {
  require_solution
  if [ "$FIX" -eq 1 ]; then
    run_dotnet "Format (applying)" format "$SLN"
  else
    run_dotnet "Lint" format "$SLN" --verify-no-changes
  fi
}

cmd_build() {
  require_solution
  run_dotnet "Restore" restore "$SLN"
  run_dotnet "Build ($CONFIG)" build "$SLN" -c "$CONFIG" --no-restore
}

# Not routed through run_dotnet, because `dotnet test` has a failure mode that
# exit status does not report: it runs ZERO tests and exits 0. Both variants
# were measured here on 2026-09-21, and each exits 0:
#
#   "No test is available"                 — no adapter registered. A project
#       with xunit.v3 and Microsoft.NET.Test.Sdk but WITHOUT
#       xunit.runner.visualstudio ran none of its three tests, green.
#   "No test matches the given testcase filter"
#                                          — the filter excluded everything,
#       or the project genuinely has no tests yet.
#
# Either way a whole suite can silently stop running with every signal green,
# which is why "ran nothing" is a failure here. This is also why CI invokes
# THIS script rather than dotnet test directly: a guard that only exists
# locally does not guard the thing that gates merges.
cmd_test() {
  require_solution
  [ -n "$FILTER" ] || FILTER=$(default_filter)

  if [ -n "$FILTER" ]; then
    say "Test ($CONFIG, filter: $FILTER)"
    note "dotnet test $SLN -c $CONFIG --no-build --filter '$FILTER'"
    set -- test "$SLN" -c "$CONFIG" --no-build --filter "$FILTER"
  else
    say "Test ($CONFIG, no filter)"
    note "dotnet test $SLN -c $CONFIG --no-build"
    set -- test "$SLN" -c "$CONFIG" --no-build
  fi

  if [ -n "$RESULTS_DIR" ]; then
    note "results and coverage -> $RESULTS_DIR"
    set -- "$@" --logger trx --collect "XPlat Code Coverage" --results-directory "$RESULTS_DIR"
  fi

  log=$(mktemp "${TMPDIR:-/tmp}/lorefetch.XXXXXX")
  status=0
  dotnet "$@" > "$log" 2>&1 || status=$?

  # dotnet test INDENTS its summary lines, so this must not be anchored at the
  # start of the line. It was, once, and the "did anything run?" check below
  # then mis-fired on a run where 52 tests had in fact passed.
  summaries=$(grep -cE '^[[:space:]]*(Passed!|Failed!)' "$log" || true)
  grep -E '^[[:space:]]*(Passed!|Failed!)' "$log" | sed 's/^ */    /' || true

  # A missing adapter is always a hard failure, even if other projects ran:
  # it means a test project is misconfigured and is silently testing nothing.
  if grep -q 'No test is available' "$log"; then
    printf '\n'
    note "FAILED: a test project discovered NO TESTS and dotnet test would have"
    note "exited 0. That message means no test adapter was registered — every"
    note "test project needs xunit.runner.visualstudio alongside xunit.v3, or"
    note "it silently stops testing anything."
    grep 'No test is available' "$log" \
      | sed 's/.* in //; s|.*/||; s/\.dll[.,].*/.dll/; s/^/    /' | sort -u | head -8
    rm -f "$log"; exit 1
  fi

  # "No test matches the given testcase filter" is NOT a failure by itself.
  # A project with no tests yet reports it, and so does one whose every test
  # the platform filter excludes — neither is wrong. What IS a failure is the
  # whole run matching nothing, because then dotnet test exits 0 having tested
  # nothing at all. So: fail only when nothing ran anywhere, and otherwise name
  # the empty projects so they cannot quietly stay empty.
  empty=$(grep 'No test matches the given testcase filter' "$log" \
            | sed 's/.* in //; s|.*/||' | sort -u || true)

  if [ "$summaries" -eq 0 ]; then
    printf '\n'
    note "FAILED: the whole run matched NO TESTS, and dotnet test would have"
    note "exited 0. Either the filter is wrong or nothing has tests yet."
    note "Filter: ${FILTER:-(none)}"
    [ -n "$empty" ] && printf '%s\n' "$empty" | sed 's/^/    /'
    rm -f "$log"; exit 1
  fi

  if [ "$status" -ne 0 ]; then
    # The failed tests first: xUnit prints each one's name, message and stack
    # as it happens, usually far above the last 60 lines, and a CI log that
    # can't say which test failed can't be diagnosed. A block starts at a
    # "Failed <test>" line (not the "Failed!" summary) and ends at the next
    # result, runner line or blank line.
    failures=$(awk '
      /^[[:space:]]*Failed [^!]/ { p = 1; print; next }
      p && (/^[[:space:]]*(Passed|Skipped|Failed)[ !]/ || /^\[xUnit/ || /^[[:space:]]*$/) { p = 0 }
      p { print }
    ' "$log" | head -300)
    if [ -n "$failures" ]; then
      printf '\n--- failed tests ---\n%s\n' "$failures"
    fi
    printf '\n--- dotnet test failed (exit %s); last 60 lines ---\n' "$status"
    tail -60 "$log"; rm -f "$log"; exit "$status"
  fi

  if [ -n "$empty" ]; then
    printf '\n'
    note "NOTE: these test projects matched no tests. Expected only while a new"
    note "project is empty; otherwise check the filter and the test adapter."
    printf '%s\n' "$empty" | sed 's/^/      /'
  fi

  rm -f "$log"
}

cmd_run() {
  require_solution
  if ! APP=$(find_app); then
    say "No app to run yet"
    note "No executable project under $REPO/src."
    exit 0
  fi
  say "Run ($CONFIG)"
  note "$APP ${APP_ARGS:+-- $APP_ARGS}"
  # Not through run_dotnet: the app is interactive, so its output goes straight
  # to the terminal rather than into a log that only surfaces on failure.
  if [ -n "$APP_ARGS" ]; then
    # shellcheck disable=SC2086
    dotnet run --project "$APP" -c "$CONFIG" --no-build -- $APP_ARGS
  else
    dotnet run --project "$APP" -c "$CONFIG" --no-build
  fi
}

# --------------------------------------------------------------------------
case "$CMD" in
  setup)  cmd_setup ;;
  doctor) cmd_doctor ;;
  pull)   cmd_pull ;;
  build)  [ "$DO_PULL" = yes ] && cmd_pull; cmd_build ;;
  test)   [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_test ;;
  lint)   [ "$DO_PULL" = yes ] && cmd_pull; cmd_lint ;;
  run)    [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_run ;;
  all)    [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_test; cmd_run ;;
esac

say "Done"
