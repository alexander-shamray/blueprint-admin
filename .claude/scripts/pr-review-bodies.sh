#!/usr/bin/env bash
# List a PR's review BODIES as JSON — /review-copilot's step-1 intake, and the
# feed that matters most: the `<details><summary>Suppressed comments</summary>`
# block arrives here, and ship.md records it as where every real finding
# against this machinery has actually come from.
#
# **New with #56, and it is the half that made the fix worth doing.** An
# earlier revision of review-copilot.md's residual named only the inline feed;
# filtering that one and leaving this one raw is a control that reads as
# complete while the important feed stays open. This helper exists so that
# `gh pr view --json reviews` need not be granted to a command holding `Edit`.
#
# stdout is the admitted subset as a JSON array — the `reviews` array unwrapped,
# not the `{"reviews": [...]}` envelope, so all three feed helpers hand back the
# same shape. The dropped count and locations go to stderr; bodies go nowhere.
#
# `gh pr view` loads `reviews` and `comments` through one GraphQL exporter, so
# the login here is the bare `copilot-pull-request-reviewer` — NOT the `[bot]`
# suffix, which is REST's spelling from /pulls/{n}/reviews and reaches no helper
# in this directory. copilot-authors.sh admits all three regardless.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-review-bodies.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
# **The same bounded page as `pr-issue-comments.sh`, and on the feed that
# matters most (#22).** `gh pr view --json reviews` is a GraphQL connection
# with no cursor path, so a first page was reported as the whole feed — and
# this is where the `<details><summary>Suppressed comments</summary>` block
# arrives, which `ship.md` records as where every real finding against this
# machinery has actually come from. A page-shaped answer here is a suppressed
# finding missed with nothing indicating it.
#
# Cursor-paginated for the reason and in the shape the sibling helper argues.
#
# **`id` and `commit.oid` are part of the contract, not decoration.** `/ship`'s
# resume proves a clean review belongs to the pushed head through the review's
# commit oid, and the paginated query first shipped without it — so a resume
# could neither establish all-resolved nor tell a stale verdict from a current
# one. Raised by Copilot.
owner=$(gh repo view --json owner --jq .owner.login)
repo=$(gh repo view --json name --jq .name)
#
# **Fetched whole before anything is filtered.** Piped straight into the
# partition, a failed fetch still reached it: the filter printed its count line
# for an empty feed, which reads as "Copilot said nothing" beside an exit code
# a caller may not check — the fail-open `grok-ledger.sh` records as #51.
# Measured during a network outage on PR #25.
feed=$(gh api graphql --paginate --slurp -f query='
  query($owner:String!,$repo:String!,$pr:Int!,$endCursor:String){
    repository(owner:$owner,name:$repo){
      pullRequest(number:$pr){
        reviews(first:100, after:$endCursor){
          pageInfo{ hasNextPage endCursor }
          nodes{ id author{ login } body state url submittedAt commit{ oid } }
        }
      }
    }
  }' -F owner="$owner" -F repo="$repo" -F pr="$pr") ||
  { echo "the review feed could not be fetched; printing nothing rather than an empty one" >&2
    exit 3; }
jq '[ .[].data.repository.pullRequest.reviews.nodes[] ]' <<<"$feed" |
  copilot_partition "$admitted" '.author.login' '.submittedAt' 'review bodies'
