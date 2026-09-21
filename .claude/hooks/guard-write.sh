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
#   2. Core/Abstractions, Core/Scanning and the project files are FROZEN once the four
#      streams fork. A unilateral edit there is a four-way merge conflict,
#      and the contracts are the only reason the streams are independent.
#      Enforced only inside linked worktrees, so Stream 0 on main can still
#      author them.
#
# Reads the PreToolUse payload on stdin, emits a permissionDecision.

set -eu

# The main checkout: the parent of the shared git dir, so every linked
# worktree (which lives under .claude/worktrees/) resolves to the same root.
# Derived rather than hardcoded so the guard works on the Windows PC too.
REPO=$(cd "$(git rev-parse --git-common-dir 2>/dev/null)/.." 2>/dev/null && pwd -P || echo "/Users/jnapoli/Repos/LoreFetch")

input=$(cat)
path=$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')

# Nothing to inspect (some tools omit file_path) — stay out of the way.
[ -z "$path" ] && exit 0

# Windows: tools pass C:\Repos\..., while pwd -P under Git Bash says /c/Repos/...
case "$path" in
  [A-Za-z]:\\*|[A-Za-z]:/*) command -v cygpath >/dev/null 2>&1 && path=$(cygpath -u "$path") ;;
esac

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
# Rule 1: project state lives in the repo
# ---------------------------------------------------------------------------
case "$abs" in
  "$REPO"/*|"$REPO")
    ;;                                    # inside the repo — fine
  /private/tmp/claude-*|/tmp/claude-*|/private/var/folders/*|/tmp/claude/*)
    ;;                                    # scratchpad / temp — fine
  *)
    deny "LoreFetch keeps all project state in the repository. '$abs' is outside $REPO.

This is the failure that lost the previous version of this project: its
memory pointed at ~/.claude/plans/<file>, and that file no longer existed.

Write documents under $REPO (docs/ or CLAUDE.md) so they are version
controlled and survive a context drop. Claude's scratchpad is exempt for
genuinely temporary files."
    ;;
esac

# ---------------------------------------------------------------------------
# Rule 2: frozen contract surface, inside linked worktrees only
# ---------------------------------------------------------------------------
# In the main checkout, --absolute-git-dir == --git-common-dir. In a linked
# worktree the former is <common>/worktrees/<name>, so they differ. $PWD
# decides which worktree we are in, so these are resolved relative to it.
# Both are resolved by the shell rather than with --path-format=absolute,
# which git < 2.31 echoes back as a literal line — making the main checkout
# look like a linked worktree and freezing the surface Stream 0 must author.
wt_gitdir=$(cd "$(git rev-parse --git-dir 2>/dev/null)" 2>/dev/null && pwd -P || echo "")
wt_common=$(cd "$(git rev-parse --git-common-dir 2>/dev/null)" 2>/dev/null && pwd -P || echo "")

in_linked_worktree=0
if [ -n "$wt_gitdir" ] && [ -n "$wt_common" ] && [ "$wt_gitdir" != "$wt_common" ]; then
  in_linked_worktree=1
fi

if [ "$in_linked_worktree" -eq 1 ]; then
  case "$abs" in
    */Core/Abstractions/*|*/LoreFetch.Core/Abstractions/*)
      deny "Core/Abstractions is FROZEN once the streams fork, and this is a linked worktree.

The contracts are the only reason streams A-D can run in parallel. A
unilateral change here is a four-way merge conflict.

If a contract genuinely needs to change: stop, say what and why, and change
it once on main so every stream rebases onto the same surface."
      ;;
    */Core/Scanning/*|*/LoreFetch.Core/Scanning/*)
      deny "Core/Scanning is FROZEN once the streams fork, and this is a linked worktree.

The scan pipeline is contract surface: it is what composes every stream's
implementation, and the end-to-end suite that guards the fork tests it.

If the pipeline genuinely needs to change: stop, say what and why, and
change it once on main so every stream rebases onto the same surface."
      ;;
    *.csproj|*.slnx|*.sln)
      deny "Project files are FROZEN once the streams fork, and this is a linked worktree.

Stream 0 creates every project with every package reference precisely so no
stream ever edits a .csproj — that is the main merge-conflict source removed
by construction.

If a package is genuinely missing: stop and add it once on main."
      ;;
  esac
fi

exit 0
