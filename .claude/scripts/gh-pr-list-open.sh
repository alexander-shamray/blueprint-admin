#!/usr/bin/env bash
# Every open pull request, with a FIXED field set, for `/ship`'s reconcile of
# `docs/todo.md` before it merges.
#
# **A helper rather than `Bash(gh pr list:*)`, because that grant is a feed.**
# `gh pr list --json reviews,comments` returns full review bodies, which is the
# bypass `CopilotFeedHelpersAreTheOnlyIntake` in `test_grok_helpers.py` refuses
# (#56). The reconcile needs a number, a title and a branch; the caller chooses
# nothing else.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: gh-pr-list-open.sh   (no arguments)" >&2; exit 2; }

repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }

# `gh pr list` has no `--paginate`, so the cap is detected rather than removed,
# as `gh-issue-list.sh` argues: a listing holding exactly the limit is refused,
# because a truncated one would drop rows from the task list silently.
LIMIT=1000
rows=$(gh pr list --repo "$repo" --state open --limit "$LIMIT" \
         --json number,title,headRefName)
[ "$(jq 'length' <<<"$rows")" -lt "$LIMIT" ] ||
  { echo "gh pr list returned exactly $LIMIT open PRs, so the listing is truncated" >&2; exit 3; }
printf '%s\n' "$rows"
