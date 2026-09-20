#!/usr/bin/env bash
# Land one of THIS repository's pull requests by rebase, bound to a named head,
# and nothing else — `/ship` step 7's one merge and no other.
#
# **`Bash(gh pr merge --merge:*)` is a PREFIX grant, so a trailing flag is
# inside it (#16).** `gh pr merge --merge <n> --admin` bypasses the failing-check
# stop that step 7 treats as mandatory: a pull request merged past red checks by
# a chain whose report says the checks gated it. `ship.md` argues this at length
# and what stood was visibility rather than prevention — step 7 reports the
# literal invocation it ran, so the claim is checkable rather than enforced.
#
# It also explains why a session could not simply write this helper: a session
# able to add `.claude/scripts/gh-pr-merge.sh` could also edit the one it is
# about to invoke, which would make every fixed endpoint in the chain a
# fiction. So this arrives as a human's change to a reviewed file, with the
# raw grant withdrawn in the same commit, which is the only way it means
# anything.
#
# **`--match-head-commit` is a required ARGUMENT here, not an optional flag.**
# `ship.md` calls it "the only guard in this step that fails closed" and then
# relied on prose to make it present. Without it the green verdict and the
# merge are two reads of a moving target: a push landing between them merges a
# commit whose checks never ran. A helper that can be called without it is a
# helper that will be.
#
# The method is fixed to `--rebase`, so `main` carries each commit `/commit`
# split rather than one `Merge pull request #n from …` per branch. Neither
# `--merge` nor `--squash` is a choice this endpoint offers: the commits are
# what `/commit` splits for a reviewer and `/pr` writes its body from, and
# squashing discards them.
#
# **A rebase landing replays the branch's commits onto `main` with new shas**,
# so nothing downstream may judge "landed" by whether the branch's own commits
# are ancestors of `main`. `ship.md` step 0's finished predicate asks instead
# whether the local tip is still the head the pull request merged — an identity
# no landing method moves.
set -euo pipefail
[ "$#" -eq 2 ] ||
  { echo "usage: gh-pr-merge.sh <pr-number> <head-oid>" >&2; exit 2; }
pr="$1"
oid="$2"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number: $pr" >&2; exit 2; }
[[ "$oid" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "head oid must be a full 40-character sha: $oid" >&2; exit 2; }

# The repository is resolved from the checkout, never accepted, so `--repo`
# cannot be aimed elsewhere by anything a body or a review said. Blank counts
# as missing: `gh` printing an empty string exits 0, and an empty `--repo`
# would fall back to whatever the checkout resolves to at merge time.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 3; }

# **The number is the caller's, so it is checked against the checkout.**
# `--match-head-commit` binds the merge to an oid, and the caller supplies
# that too: any open PR in this repository, named with its own head, passed.
# So the PR must be the one for the branch checked out here, from this
# repository rather than a fork, with that head, and into `main` — the only
# base `/pr` opens against, so a PR from this branch into anything else is not
# the delivery this helper exists for. Raised by Copilot.
branch=$(git branch --show-current)
[ -n "$branch" ] && [ "$branch" != main ] ||
  { echo "not on a PR branch: there is no branch to bind pull request #$pr to" >&2; exit 3; }
head=$(gh pr view "$pr" --repo "$repo" \
  --json headRefName,headRefOid,isCrossRepository,baseRefName \
  --jq '[.headRefName, .headRefOid, (.isCrossRepository|tostring), .baseRefName] | @tsv') ||
  { echo "cannot read pull request #$pr" >&2; exit 3; }
IFS=$'\t' read -r head_branch head_oid cross base <<<"${head%$'\r'}"
[ "$base" = main ] ||
  { echo "pull request #$pr targets $base, not main" >&2; exit 3; }
[ "$cross" = false ] ||
  { echo "pull request #$pr comes from another repository" >&2; exit 3; }
[ "$head_branch" = "$branch" ] ||
  { echo "pull request #$pr is for $head_branch, not the checked-out $branch" >&2; exit 3; }
[ "$head_oid" = "$oid" ] ||
  { echo "pull request #$pr's head is $head_oid, not $oid" >&2; exit 3; }

gh pr merge --rebase --repo "$repo" --match-head-commit "$oid" "$pr"
