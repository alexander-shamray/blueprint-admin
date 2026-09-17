#!/usr/bin/env bash
# List a PR's issue comments as JSON — /review-copilot's step-3 intake.
#
# **New with #56.** The third of the three feeds, and the one whose Copilot
# login has never been observed: six PRs were checked at filing time — #112,
# #101, #100, #99, #98, #94 — and not one carried a Copilot-authored issue
# comment. So the admitted spelling here is what `gh pr view`'s shared GraphQL
# exporter MUST report if Copilot ever posts to this feed, and nothing has seen
# it do so. That is written down in review-copilot.md's feed table as an
# inference rather than a measurement, and it is repeated here rather than
# quietly relied on.
#
# The filter is worth having on exactly that account. This feed carries no
# observed Copilot traffic and is wide open to every other account, so an
# unfiltered read of it is a pure intake of strangers' text into a command that
# holds `Edit`. It is the feed with the worst ratio of the three.
#
# stdout is the admitted subset as a JSON array — the `comments` array
# unwrapped, matching the other two helpers. Dropped count and locations to
# stderr; bodies nowhere.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-issue-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
# **`gh pr view --json comments` is a GraphQL connection with no cursor path
# (#22).** It returns a first page and reports it as the whole feed, so later
# comments were hidden BEFORE `copilot_partition` saw them — and the helper
# then printed an admitted/dropped count that reads as a complete filter over
# an incomplete feed. `/review-copilot` reports those counts as the evidence
# its filter ran, so the number would be true of the page rather than of the
# pull request, and a suppressed finding on a long PR could be missed with
# nothing indicating it.
#
# So the query is spelled here and cursor-paginated. `--paginate` requires the
# variable to be named `$endCursor` and the connection to return `pageInfo`;
# `--slurp` wraps the pages in an array, which is the shape
# `copilot-request-count.sh` already argues for — "with --paginate, a per-page
# --jq emits one number per page, and a busy PR would hand the loop several
# where it expects one." The field set stays fixed, which is what this helper
# is for.
owner=$(gh repo view --json owner --jq .owner.login)
repo=$(gh repo view --json name --jq .name)
#
# Fetched whole before anything is filtered, for the reason
# `pr-review-bodies.sh` gives: a failed fetch piped into the partition printed
# an empty feed's count line.
feed=$(gh api graphql --paginate --slurp -f query='
  query($owner:String!,$repo:String!,$pr:Int!,$endCursor:String){
    repository(owner:$owner,name:$repo){
      pullRequest(number:$pr){
        comments(first:100, after:$endCursor){
          pageInfo{ hasNextPage endCursor }
          nodes{ author{ login } body url createdAt }
        }
      }
    }
  }' -F owner="$owner" -F repo="$repo" -F pr="$pr") ||
  { echo "the issue comment feed could not be fetched; printing nothing rather than an empty one" >&2
    exit 3; }
jq '[ .[].data.repository.pullRequest.comments.nodes[] ]' <<<"$feed" |
  copilot_partition "$admitted" '.author.login' '.url' 'issue comments'
