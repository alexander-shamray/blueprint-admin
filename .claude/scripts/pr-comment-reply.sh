#!/usr/bin/env bash
# Reply to one inline review comment — the reasoned replies and the one-word
# markers of /review-copilot. The endpoint is fixed, so the grant covers
# posting a reply on this repository's PR threads and nothing else.
#
# **Takes no arguments.** The PR number, the comment id and the body used to
# be positional, and the documented caller wrapped the body in single quotes.
# An apostrophe in that text — a Copilot finding, or our own reply — closed
# the quote in the parent shell and ran whatever followed, before this script
# started, so no check here could reach it. The three inputs are therefore
# files at the checkout root, the same channel `gh-pr-create.sh` uses for
# title and body. Raised by Copilot.
set -euo pipefail
[ "$#" -eq 0 ] ||
  { echo "usage: pr-comment-reply.sh — reads pr-reply.pr, pr-reply.id and pr-reply.body at the checkout root" >&2
    exit 2; }

checkout=$(git rev-parse --show-toplevel) ||
  { echo "not inside a git checkout" >&2; exit 3; }
checkout=$(cd "$checkout" && pwd -P)
pr_file="$checkout/pr-reply.pr"
id_file="$checkout/pr-reply.id"
body_file="$checkout/pr-reply.body"

for file in "$pr_file" "$id_file" "$body_file"; do
  [ ! -L "$file" ] ||
    { echo "a symbolic link is not a reply input: $file" >&2; exit 2; }
  [ -f "$file" ] ||
    { echo "the caller-created file is missing: $file" >&2; exit 2; }
  # Caller-created means not the branch's. A tracked input is text the branch
  # under review chose. `.gitignore` names all three, so only a force-add
  # reaches here.
  ! git -C "$checkout" ls-files --error-unmatch "${file##*/}" >/dev/null 2>&1 ||
    { echo "${file##*/} is tracked by this branch; it must be written for this run" >&2
      exit 2; }
done

pr=$(cat -- "$pr_file")
cid=$(cat -- "$id_file")
[ -n "$pr" ] && [ -n "$cid" ] || { echo "ids must be numbers" >&2; exit 2; }
[[ "$pr" =~ ^[0-9]+$ && "$cid" =~ ^[0-9]+$ ]] || { echo "ids must be numbers" >&2; exit 2; }
case "$pr$cid" in *$'\n'*|*$'\r'*)
  echo "an id spans more than one line" >&2; exit 2 ;;
esac

body=$(cat -- "$body_file")
[ -n "$body" ] || { echo "the body is empty" >&2; exit 2; }

gh api "repos/{owner}/{repo}/pulls/$pr/comments/$cid/replies" -f body="$body" --jq .id

# Removed once posted, and only then: a failed post keeps them for the retry.
# Left behind, they were untracked files every triage produced, which
# `/ship`'s later clean-tree gate reads as work to commit.
rm -f -- "$pr_file" "$id_file" "$body_file"
