#!/usr/bin/env bash
# Is `main` red? Prints the open `main is red` issue and exits 3, or prints
# nothing and exits 0. Read-only, no arguments, fixed field set.
#
# **A pull request's own checks cannot answer this (#531).** A `pull_request`
# run is pinned to the merge ref of the push that started it, and nothing
# reruns it when `main` moves, so a pull request last pushed before `main` went
# red still reads green in `gh pr checks`. `.github/workflows/main-red.yml`
# keeps one issue open while `ci` on `main`'s tip is red, and `/ship` step 6
# reads it here: an open one stops the landing as a red check does.
#
# **The query is the workflow's own, so this sees what that opens and
# closes.** The workflow's `TITLE` owns the title, and a test holds the value
# below to it. The search is full text, so a title that merely contains the
# phrase comes back too; equality on the title decides, as it does there.
#
# **Fail closed.** A listing that could not be taken is not "no issue": it
# exits 2, and every exit but 0 stops the chain. The page is bounded and a
# full one refused, the shape `gh-issue-list.sh` argues. No argument and no
# field is the caller's, so the grant reaches no issue body.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: main-red-open.sh   (no arguments)" >&2; exit 2; }

TITLE="main is red"
LIMIT=100
rows=$(gh issue list --state open --label bug --search "in:title \"$TITLE\"" \
         --limit "$LIMIT" --json number,title,url) ||
  { echo "cannot list the open issues, so whether main is red is unknown" >&2; exit 2; }
[ "$(jq 'length' <<<"$rows")" -lt "$LIMIT" ] ||
  { echo "gh issue list returned exactly $LIMIT issues, so the listing may be truncated" >&2; exit 2; }
open=$(jq -r --arg title "$TITLE" \
  '.[] | select(.title == $title) | "#\(.number) \(.url)"' <<<"$rows")
[ -z "$open" ] || { printf 'main is red: %s\n' "$open"; exit 3; }
