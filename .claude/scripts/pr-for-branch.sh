#!/usr/bin/env bash
# List the pull requests for one branch — number, state, url, headRefOid —
# and nothing else. Read-only, fixed field set.
#
# **`headRefOid` is published because `ship.md` step 0 decides with it.** That
# step calls a branch finished when the head the pull request merged reaches
# the local tip — that head, or a tip behind it, which a checkout another
# session pushed past will have. A tip it cannot reach carries work done after
# the merge. Only the pull request knows that oid.
#
# **Exists because `gh pr list` reaches the review feeds (#56).** Removing
# `Bash(gh pr view:*)` from the three commands that held it was not enough:
# `gh pr list --json reviews,comments` returns the same review bodies and issue
# comments, in full, for every pull request at once. Measured, not reasoned —
# `gh pr list --state all --limit 1 --json number,reviews` on this repository
# returned a 2,457-character Copilot review body. So the grant those commands
# kept for the harmless job of finding a branch's PR was a complete bypass of
# all three author-filtering helpers.
#
# That is the same defect as the `gh pr view` one, one subcommand over, and it
# is why the test that pins this invariant now enumerates the *commands that
# can reach the fields* rather than the one spelling that was fixed first.
#
# The branch is optional and defaults to the checkout's current branch. When
# given it is shape-checked, because it reaches an argument position: a value
# starting with `-` would be read as a flag, and `gh pr list` has flags that
# change what is returned.
set -euo pipefail
branch="${1:-}"
if [ -z "$branch" ]; then
  branch=$(git branch --show-current)
  [ -n "$branch" ] || { echo "detached HEAD and no branch given" >&2; exit 2; }
fi
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# **`--head` filters on the branch NAME and nothing else**, which is not the
# question being asked. It matches across forks, so an outside contributor's
# pull request from a same-named branch is a candidate here — and /ship step 0
# reads this to decide whether the branch landed, /pr to decide whether one is
# already open. Acting on a stranger's pull request is the failure.
#
# grok-review.sh:139 already carries this check and the argument for it, and
# this helper shipped without it: a fix that closes a hole by name and leaves
# it open by provenance has moved the defect rather than removed it — written
# down one file away, in a comment, and reimplemented wrong here anyway.
#
# Both sides of the comparison are properties of the filesystem: `gh repo view`
# reads the checkout, and the branch came from `git branch --show-current` or
# was shape-checked above. The value reaches jq through `--arg`, never as
# program text.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# **Blank counts as missing, and `||` does not see it.** `gh` printing an empty
# string exits 0, so the guard above passes and `$repo` is "" — and the
# comparison below then matches every row whose head repository is absent,
# which `// ""` renders as "" too. A deleted fork reports exactly that. So the
# filter would admit a stranger's pull request precisely when it could not
# establish whose it was, which is the fail-open direction.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
# **`--head` matches a branch NAME, so this returned every pull request that
# has ever used it, in whatever order the API gave (#24).** A branch name
# reused after a merge — ordinary, and nothing in `/branch` prevents it —
# leaves an older `MERGED` row beside a newer `OPEN` one. `/ship` step 0 reads
# a `MERGED` row as proof the branch is finished and tears the workspace down;
# `/pr` has the mirror of it and creates a duplicate. That is the worst answer
# this chain can give: not a refusal and not a red check, but a confident
# teardown of live work, justified by a true statement about a different pull
# request.
#
# So the selection is bounded here rather than left to the caller: the rows are
# filtered to this repository, sorted, and **the newest alone is returned**.
# `ship.md` already says "the newest row" in as many words; this is what makes
# that true. Sorted by `number`, which GitHub assigns monotonically per
# repository, so it needs no date parsing to be a total order.
#
# **And the page is bounded where the field set was not (#22).** `gh pr list`
# defaults to 30 and has no `--paginate`; a popular branch name across forks
# can fill it, and this helper's own repository filter runs AFTER the page is
# taken — so the row that matters could be the one left off. `--limit` pages
# internally, and a response holding exactly the limit is refused rather than
# returned, because a truncated listing here is a wrong answer and not a
# smaller one.
limit=1000
rows=$(gh pr list --state all --head "$branch" --limit "$limit" \
         --json number,state,url,headRepository,headRefOid,baseRefName,mergeCommit)
[ "$(jq 'length' <<<"$rows")" -lt "$limit" ] ||
  { echo "gh pr list returned exactly $limit rows for $branch, so the listing may be truncated and the newest row cannot be established" >&2; exit 4; }
# **Into `main` only.** GitHub lets one head branch open PRs against several
# bases, so a newer PR from this branch into `develop` was the newest row and
# masked the `main` PR: `/pr` refused to open the right one and `/ship`
# reviewed the wrong one until `gh-pr-merge.sh` refused it at the end. Every
# PR this chain opens targets `main`, so no other base is its PR. Raised by
# Copilot.
newest=$(jq --arg repo "$repo" \
  '[ .[] | select((.headRepository.nameWithOwner // "") == $repo)
         | select(.baseRefName == "main") ]
   | sort_by(.number) | reverse | .[0:1]' <<<"$rows")

# **The newest row can still be a previous incarnation of the branch.** Sorting
# settles a reused name once the new PR exists; before that, the old `MERGED`
# row is the only row, and `/ship` step 0 read new work on the reused branch as
# already delivered. Raised by Copilot.
#
# The local branch says which incarnation this is, and **the landing commit is
# the test, not the PR's head** — `mergeCommit`, which under a rebase landing
# names the last replayed ordinary commit and under a merge landing names the
# merge itself. Either way it is on `main`, and that is what the test needs.
#
# The PR's head cannot serve: a merged row whose head is a strict ancestor of
# the tip describes a branch recreated from `main` AND the original branch
# with commits added after its PR merged, and `ship.md` stops on the second
# rather than treating it as a stale row. A branch recreated from a `main`
# that carries the landing commit contains it; the original branch with
# commits on top does not. So the row is dropped only when the landing commit
# is an ancestor of the tip. A landing commit this checkout has not fetched,
# or no local branch at all, leaves the row as reported — the stop is the safe
# answer when this cannot be shown. An open row is never dropped: an open PR
# is the branch's current one by definition.
#
# **Merged only, never closed.** A PR closed unmerged is somebody's decision
# that this branch does not land, and `/ship` stops on it; dropping the row
# after one more local commit sent the run to `/pr` to open a replacement over
# that decision. Raised by Copilot.
state=$(jq -r '.[0].state // ""' <<<"$newest")
if [ "$state" = MERGED ]; then
  merge_oid=$(jq -r '.[0].mergeCommit.oid // ""' <<<"$newest")
  tip=$(git rev-parse --verify --quiet "refs/heads/$branch" || true)
  if [ -n "$merge_oid" ] && [ -n "$tip" ] &&
     git merge-base --is-ancestor "$merge_oid" "$tip" 2>/dev/null; then
    newest='[]'
  fi
fi
jq '[ .[] | {number, state, url, headRefOid} ]' <<<"$newest"
