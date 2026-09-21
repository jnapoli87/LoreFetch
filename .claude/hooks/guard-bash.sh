#!/bin/sh
#
# PreToolUse guard for Bash. One hard block, one tripwire.
#
#   DENY  force-push. Rewriting published history on a repo that is already
#         public is not recoverable for anyone who has cloned it.
#
#   WARN  plain `git push`, `gh pr create`, `gh repo create`. These publish.
#         Deliberately NOT a block: the user does give explicit go-aheads and
#         a wall would make pushing impossible. This is a tripwire that
#         re-raises the rule at the moment of action — the rule being that
#         content is shown and approved BEFORE it lands somewhere other
#         people can see it.
#
# Reads the PreToolUse payload on stdin.

set -eu

input=$(cat)
cmd=$(printf '%s' "$input" | jq -r '.tool_input.command // empty')

[ -z "$cmd" ] && exit 0

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

warn() {
  jq -cn --arg m "$1" '{systemMessage: $m}'
  exit 0
}

# ---------------------------------------------------------------------------
# Force-push: blocked outright
# ---------------------------------------------------------------------------
# Deliberately loose: `git ... push` anywhere in the command. An earlier
# attempt matched only flag NAMES between `git` and `push`, so it missed
# `git -C /some/repo push --force` — a flag VALUE broke the pattern. Loose is
# the safe direction: the deny below additionally requires a force flag, so a
# false-positive push match can only ever produce a warning.
if printf '%s' "$cmd" | grep -Eq '\bgit\b.*\bpush\b'; then
  if printf '%s' "$cmd" | grep -Eq -- '(--force-with-lease|--force\b|[[:space:]]-f\b)'; then
    deny "Force-push is blocked in this repository.

LoreFetch is published, so rewriting history is not recoverable for anyone
who has already cloned it — and the repo's own history is the audit trail for
commit authorship and the no-card-imagery rule.

If history genuinely must be rewritten, do it deliberately and by hand, not
as part of an automated push."
  fi

  warn "git push publishes to github.com/jnapoli87/LoreFetch — visible to other people and awkward to un-publish. Confirm the content was shown and approved first."
  exit 0
fi

# ---------------------------------------------------------------------------
# Other publishing surfaces: warn
# ---------------------------------------------------------------------------
if printf '%s' "$cmd" | grep -Eq 'gh[[:space:]]+(pr[[:space:]]+create|repo[[:space:]]+create|release[[:space:]]+create)'; then
  warn "This gh command publishes externally. Show the content and get explicit approval before running it."
  exit 0
fi

exit 0
