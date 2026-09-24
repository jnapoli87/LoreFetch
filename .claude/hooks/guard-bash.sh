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
# Committing with the git hook unwired: blocked
# ---------------------------------------------------------------------------
# This closes a hole that is invisible until it has already happened.
# `hooks/pre-commit` is TRACKED, so the file arrives in a fresh clone — but
# `core.hooksPath` is CONFIG, and config does not clone. With `user.email`
# also unset, git falls back to the global identity, which on these machines
# is a work address. So a fresh clone commits under the wrong identity into a
# PUBLIC repo with the hook that exists to refuse exactly that not running at
# all. Verified on a real clone, 2026-09-21.
#
# This file lives under .claude/, which DOES clone, so it is here rather than
# in the git hook: it is the only guard that can still speak when the git
# hook is switched off. It covers Claude-driven commits in any clone. A human
# committing by hand is covered by the README and by
# `scripts/lorefetch.sh doctor`, which exits non-zero when the guards are
# not live.
#
# Checked in the same segment as `commit` (see the force-push note below for
# why segment-scoping matters). In a correctly wired checkout this never
# fires, so the cost in the normal case is one `git config` read.
if printf '%s' "$cmd" | grep -Eq '\bgit\b[^;&|]*\bcommit\b'; then
  hp=$(git config core.hooksPath 2>/dev/null || true)
  if [ "$hp" != "hooks" ]; then
    deny "This checkout's commit guards are NOT wired, so committing is blocked.

core.hooksPath is '${hp:-unset}', expected 'hooks'. hooks/pre-commit is
tracked so the FILE is here, but core.hooksPath is local config and config
does not clone — so the hook is not running, and with user.email unset git
falls back to the global identity, which is a work address. This repo is
public.

Fix it, repo-locally, without touching your global config:

    scripts/lorefetch.sh setup

Then 'scripts/lorefetch.sh doctor' will confirm. It exits non-zero while the
guards are not live, so it is safe to trust."
  fi
fi

# ---------------------------------------------------------------------------
# Force-push: blocked outright
# ---------------------------------------------------------------------------
# DETECTING the push is deliberately loose: `git ... push` anywhere in the
# command. An earlier attempt matched only flag NAMES between `git` and `push`,
# so it missed `git -C /some/repo push --force` — a flag VALUE broke the
# pattern. Loose is the safe direction here, because a loose match only ever
# adds the warning.
#
# DENYING is a separate question, and the first version got it wrong. It
# searched the WHOLE command for a force flag, so any `-f` belonging to any
# other command denied the call: `git commit -F msg && git push; rm -f tmp`
# was refused as a force-push. The claim once recorded in CLAUDE.md (now in
# docs/history/CLAUDE-v0.1.md) — that a
# false-positive push match "can only ever produce a warning, because the deny
# additionally requires a force flag" — was simply wrong. Both halves were
# loose, so the conjunction was loose too, and `rm -f` was enough to block a
# legitimate push.
#
# So the force flag must now appear AFTER `push` and in the SAME shell segment
# ([^;&|] stops at ; && || and pipes). That still catches every real form —
# `git push --force`, `git push origin main -f`, `git -C /r push
# --force-with-lease`, and any of them after a `cd x &&` — while a `-f` on an
# unrelated command in another segment no longer denies. A force flag passed
# through a variable still slips past the deny, which is why the warning below
# is unconditional.
if printf '%s' "$cmd" | grep -Eq '\bgit\b.*\bpush\b'; then
  if printf '%s' "$cmd" | grep -Eq -- '\bgit\b[^;&|]*\bpush\b[^;&|]*(--force-with-lease|--force\b|[[:space:]]-f\b)'; then
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
