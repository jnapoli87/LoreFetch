#!/bin/sh
#
# LoreFetch — pull, build, test, run.
#
# One entry point for both machines. The orchestrator builds and tests on the
# Mac and pushes; the Windows PC pulls and runs the same commands here, so
# "works on my machine" is the same machine-independent script either way.
#
#   scripts/lorefetch.sh              # pull, build, test, then run the app
#   scripts/lorefetch.sh doctor       # environment report, changes nothing
#   scripts/lorefetch.sh pull
#   scripts/lorefetch.sh build
#   scripts/lorefetch.sh test
#   scripts/lorefetch.sh run -- --demo   # everything after -- goes to the app
#
# Options
#   --no-pull          skip the pull (default for build/test/run when named
#                      explicitly; `all` pulls unless you pass this)
#   --debug            Debug instead of Release
#   --filter <expr>    override the test filter entirely
#   --hardware         include Category=Hardware tests (needs the C920)
#   --all-tests        no category filter at all
#   -v, --verbose      show full dotnet output instead of the tail
#
# POSIX sh on purpose: on Windows this runs under Git Bash, which is already
# required by hooks/pre-commit and .claude/hooks/*.sh. Do not reach for bash
# builtins here.

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
    all|pull|build|test|run|doctor)
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
    --)            shift; APP_ARGS="$*"; break ;;
    -h|--help)     sed -n '3,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             die "unknown argument: $1  (try --help)" ;;
  esac
  shift
done

[ -n "$CMD" ] || CMD=all
case "$CMD" in
  all)                  [ "$DO_PULL" = auto ] && DO_PULL=yes ;;
  build|test|run|doctor) [ "$DO_PULL" = auto ] && DO_PULL=no ;;
esac

# --------------------------------------------------------------------------
# Platform and the default test filter
# --------------------------------------------------------------------------
# Mirrors .github/workflows/ci.yml deliberately: a local run that passes where
# CI fails, or vice versa, is worse than no local run. Both legs drop Hardware;
# the macOS leg also drops WindowsOnly, because the golden hashes are generated
# on win-x64 and INTER_AREA is not bit-exact on ARM64 (CLAUDE.md, Risk 2).
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
# Deliberately discovered rather than hardcoded: this script is written before
# S0.1 creates the solution, and it has to be honest about that instead of
# failing with a confusing MSBuild error.
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

  say "Git identity (the pre-commit hook enforces this)"
  note "author   : $(git -C "$REPO" var GIT_AUTHOR_IDENT 2>/dev/null || echo '?')"
  note "committer: $(git -C "$REPO" var GIT_COMMITTER_IDENT 2>/dev/null || echo '?')"
  note "hooksPath: $(git -C "$REPO" config core.hooksPath || echo '(unset — the hook will NOT run)')"

  say "Claude Code guards"
  if command -v jq >/dev/null 2>&1; then
    note "jq       : $(jq --version)"
  else
    note "jq       : MISSING — .claude/hooks/guard-write.sh fails OPEN without it,"
    note "           so the frozen contract surface is unguarded. Install it and"
    note "           start a new shell so PATH picks it up."
  fi

  say "Solution"
  if sln=$(find_solution); then note "solution : $sln"; else
    note "solution : none yet (S0.1 has not run)"; fi
  if app=$(find_app); then note "app      : $app"; else
    note "app      : none yet"; fi
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
    note "Stream 0 package S0.1 creates the solution; until it lands there is"
    note "nothing here to build, and that is the expected state rather than a"
    note "broken checkout. 'scripts/lorefetch.sh doctor' still works."
    exit 0
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
    printf '\n--- dotnet test failed (exit %s); last 60 lines ---\n' "$status"
    tail -60 "$log"; rm -f "$log"; exit "$status"
  fi

  if [ -n "$empty" ]; then
    printf '\n'
    note "NOTE: these test projects matched no tests. Expected while a stream is"
    note "unstarted; suspicious once it is done."
    printf '%s\n' "$empty" | sed 's/^/      /'
  fi

  rm -f "$log"
}

cmd_run() {
  require_solution
  if ! APP=$(find_app); then
    say "No app to run yet"
    note "No executable project under $REPO/src. S0.1 creates LoreFetch.App."
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
  doctor) cmd_doctor ;;
  pull)   cmd_pull ;;
  build)  [ "$DO_PULL" = yes ] && cmd_pull; cmd_build ;;
  test)   [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_test ;;
  run)    [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_run ;;
  all)    [ "$DO_PULL" = yes ] && cmd_pull; cmd_build; cmd_test; cmd_run ;;
esac

say "Done"
