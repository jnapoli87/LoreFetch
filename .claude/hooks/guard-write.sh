#!/bin/sh
#
# PreToolUse guard for Write|Edit. Two rules, both structural:
#
#   1. Project state stays in the repo. Writing plans or notes to
#      ~/.claude/plans/ is how the PREVIOUS incarnation of this project was
#      lost: its memory pointed at a plan file that was later deleted.
#      Claude's scratchpad is exempt — temp files are legitimate, they just
#      are not project state.
#
#   2. The contract surface is FROZEN once the four streams fork:
#      Core/Abstractions, Core/Scanning, Core/Fakes, Tests/Integration, the
#      project files, the Directory.* files and global.json. A unilateral edit
#      there is a four-way merge conflict, and the contracts are the only
#      reason the streams are independent. Enforced only for targets inside a
#      linked worktree, so Stream 0 on main can still author them.
#
#      The linked-worktree question is answered from the TARGET FILE's
#      directory, never from $PWD: the Agent tool launches every subagent from
#      the main checkout, so a cwd-based test lets a stream implementer write
#      into .claude/worktrees/stream-x/.../Core/Abstractions unchallenged.
#
# Reads the PreToolUse payload on stdin, emits a permissionDecision.

set -eu

input=$(cat)
path=$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')

# Nothing to inspect (some tools omit file_path) — stay out of the way.
[ -z "$path" ] && exit 0

# Windows: tools pass C:\Repos\..., while pwd -P under Git Bash says /c/Repos/...
win_to_unix() {
  case "$1" in
    [A-Za-z]:\\*|[A-Za-z]:/*)
      if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s' "$1"; fi ;;
    *) printf '%s' "$1" ;;
  esac
}

path=$(win_to_unix "$path")

case "$path" in
  /*) abs="$path" ;;
  *)  abs="$PWD/$path" ;;
esac

deny() {
  jq -cn --arg r "$1" '{
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: "deny",
      permissionDecisionReason: $r
    }
  }'
  exit 0
}

# ---------------------------------------------------------------------------
# Resolve the git dirs OF THE TARGET, not of the session (V3)
# ---------------------------------------------------------------------------
# The file may not exist yet, and neither may its directory, so walk up to the
# nearest existing ancestor before asking git anything.
probe=$(dirname "$abs")
while [ ! -d "$probe" ]; do
  parent=$(dirname "$probe")
  [ "$parent" = "$probe" ] && break
  probe="$parent"
done

# git < 2.31 echoes --path-format=absolute back as a literal line, so the
# absolute form is produced by the shell instead. A relative answer (git says
# ".git" when asked from a repo root) is resolved against the probed dir.
resolve_gitdir() {  # $1 = existing directory, $2 = --git-dir | --git-common-dir
  d=$(git -C "$1" rev-parse "$2" 2>/dev/null) || return 1
  [ -n "$d" ] || return 1
  d=$(win_to_unix "$d")
  case "$d" in
    /*) ;;
    *) d="$1/$d" ;;
  esac
  (cd "$d" 2>/dev/null && pwd -P) || return 1
}

tgt_gitdir=$(resolve_gitdir "$probe" --git-dir || echo "")
tgt_common=$(resolve_gitdir "$probe" --git-common-dir || echo "")

# ---------------------------------------------------------------------------
# Rule 1: project state lives in the repo
# ---------------------------------------------------------------------------
# REPO is the main checkout: the parent of the SESSION's shared git dir, so
# every linked worktree (which lives under .claude/worktrees/) resolves to the
# same root. Derived rather than hardcoded so the guard works on the Windows PC
# too. It must come from the session, not from the target — deriving it from the
# target would make "inside some repo" pass as "inside this repo", letting a
# write into a different repository through.
sess_common=$(resolve_gitdir "$PWD" --git-common-dir || echo "")
if [ -n "$sess_common" ]; then
  REPO=$(cd "$sess_common/.." && pwd -P)
else
  REPO=$(win_to_unix "${CLAUDE_PROJECT_DIR:-}")
  [ -n "$REPO" ] && REPO=$(cd "$REPO" 2>/dev/null && pwd -P || echo "")
fi

if [ -n "$REPO" ]; then
  case "$abs" in
    "$REPO"/*|"$REPO")
      ;;                                    # inside the repo — fine
    /private/tmp/claude-*|/tmp/claude-*|/private/var/folders/*|/tmp/claude/*)
      ;;                                    # macOS scratchpad / temp — fine
    */AppData/Local/Temp/claude/*|*/AppData/Local/Temp/claude-*)
      ;;                                    # Windows scratchpad — fine
    *)
      deny "LoreFetch keeps all project state in the repository. '$abs' is outside $REPO.

This is the failure that lost the previous version of this project: its
memory pointed at ~/.claude/plans/<file>, and that file no longer existed.

Write documents under $REPO (docs/ or CLAUDE.md) so they are version
controlled and survive a context drop. Claude's scratchpad is exempt for
genuinely temporary files."
      ;;
  esac
fi

# ---------------------------------------------------------------------------
# Rule 2: frozen contract surface, for targets inside a linked worktree only
# ---------------------------------------------------------------------------
# In the main checkout, --git-dir == --git-common-dir. In a linked worktree the
# former is <common>/worktrees/<name>, so they differ.
in_linked_worktree=0
if [ -n "$tgt_gitdir" ] && [ -n "$tgt_common" ] && [ "$tgt_gitdir" != "$tgt_common" ]; then
  in_linked_worktree=1
fi

if [ "$in_linked_worktree" -eq 1 ]; then
  frozen=""
  case "$abs" in
    */Core/Abstractions/*|*/LoreFetch.Core/Abstractions/*)
      frozen="Core/Abstractions"
      why="The contracts are the only reason streams A-D can run in parallel." ;;
    */Core/Scanning/*|*/LoreFetch.Core/Scanning/*)
      frozen="Core/Scanning"
      why="The scan pipeline is contract surface: it is what composes every
stream's implementation, and the end-to-end suite that guards the fork
tests it." ;;
    */Core/Fakes/*|*/LoreFetch.Core/Fakes/*)
      frozen="Core/Fakes"
      why="The fakes are what make every stream independent of the other three.
Stream A's whole build stands on them, and FolderFrameSource is the demo
path, not just a test double." ;;
    */Tests/Integration/*)
      frozen="Tests/Integration"
      why="The end-to-end suite is the cross-worktree guard: it fails the moment
someone breaks a contract. A stream editing it can weaken the one test
that would have caught the stream." ;;
    *.csproj|*.slnx|*.sln)
      frozen="Project files"
      why="Stream 0 creates every project with every package reference precisely
so no stream ever edits a .csproj — that is the main merge-conflict source
removed by construction. If a package is genuinely missing: stop and add
it once on main." ;;
    */Directory.Build.props|*/Directory.Packages.props|*/global.json)
      frozen="The build-wide MSBuild files"
      why="Directory.Build.props, Directory.Packages.props and global.json set the
TFM, the warning policy and every central package version for all four
streams at once. A stream changing one changes the other three's build." ;;
  esac

  if [ -n "$frozen" ]; then
    deny "$frozen is FROZEN once the streams fork, and '$abs' is inside a linked worktree.

$why

If this genuinely needs to change: stop, say what and why, and change it
once on main so every stream rebases onto the same surface."
  fi
fi

exit 0
