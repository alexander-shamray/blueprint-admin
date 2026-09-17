#!/usr/bin/env bash
# Open a pull request on THIS repository, from this branch, into main — and
# nothing else. `/pr`'s one create and `/ship`'s.
#
# **`Bash(gh pr create:*)` is a PREFIX grant and every one of the four things
# that decide what gets opened is a trailing flag (#16).**
# `gh pr create --repo <other> --head <other> --base <other> --body-file <other>`
# chooses a repository, a branch, a base and a body that are not the ones the
# command derived. All four are fixed here: the repository and the branch are
# read from the checkout, the base is the literal `main`, and the body is a
# file this script validates.
#
# **The body file is where the leak would be.** `--body-file` makes `gh` read a
# file the session's own `Read` tool may be bounded away from and publish it —
# so the body is the caller-created `pr-body.md` at the checkout root and no
# other path. A broad checkout or temp-root allow-list would turn protected
# configuration and any other scratch file into publishable input.
# A symbolic link is refused rather than followed, for the reason every other
# crossing in this directory refuses one.
#
# **The title arrives through a file too, and it used to be an argument.**
# `/pr` derives the title from commit text or its own argument, and the
# documented call put it inside double quotes on the Bash command line — so a
# title such as `fix: $(…)` ran the substitution in the calling shell before
# this script saw anything, and no validation here could reach it. So the
# helper takes no argument at all: the title is the one line of
# `pr-title.txt` beside the body, written with the `Write` tool, the channel
# `gh-issue-filing.sh` already uses for untrusted titles. Raised by Copilot.
set -euo pipefail
[ "$#" -eq 0 ] ||
  { echo "usage: gh-pr-create.sh — reads pr-title.txt and pr-body.md at the checkout root" >&2
    exit 2; }

checkout=$(git rev-parse --show-toplevel) ||
  { echo "not inside a git checkout" >&2; exit 3; }
checkout=$(cd "$checkout" && pwd -P)
title_file="$checkout/pr-title.txt"
body="$checkout/pr-body.md"

for file in "$title_file" "$body"; do
  [ ! -L "$file" ] ||
    { echo "a symbolic link is not a PR input: $file" >&2; exit 2; }
  [ -f "$file" ] ||
    { echo "the caller-created file is missing: $file" >&2; exit 2; }
  # **Caller-created means not the branch's.** A tracked input is text the
  # branch under review chose, and publishing it would let the branch write
  # its own PR. `.gitignore` names both files, so only a force-add reaches
  # here. Raised by Copilot.
  ! git -C "$checkout" ls-files --error-unmatch "${file##*/}" >/dev/null 2>&1 ||
    { echo "${file##*/} is tracked by this branch; it must be written for this run" >&2
      exit 2; }
done

# One line. A newline in a title is not a title, and `gh` would take the rest
# as body text with no indication in the report of what was sent. The one
# trailing newline a file ends with is not part of it.
title=$(cat -- "$title_file")
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }
case "$title" in *$'\n'*|*$'\r'*)
  echo "the title spans more than one line" >&2; exit 2 ;;
esac

branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "detached HEAD: there is no branch to open a PR for" >&2; exit 3; }
[ "$branch" != main ] ||
  { echo "refusing to open a pull request from main into main" >&2; exit 3; }

repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 3; }

# The title is passed as a value, never spliced, so a leading `-` in it is a
# title and not a flag.
gh pr create --repo "$repo" --base main --head "$branch" \
  --title "$title" --body-file "$body"

# Removed once published, and only then: a failed create keeps both for the
# retry. Left behind, they were untracked files every `/pr` produced, which
# `/ship`'s later clean-tree gate reads as work to commit. `.gitignore` covers
# the window between writing them and this line. Raised by Copilot.
rm -f -- "$body" "$title_file"
