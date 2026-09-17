#!/usr/bin/env bash
# Map a PR's review threads: thread id, resolved state, first comment's
# database id and path — the join /review-copilot needs, because resolution
# is a GraphQL mutation on the thread id, not the comment id. Cursor-
# paginated: step 6's clean exit requires zero unresolved threads, and a
# fixed first:100 would silently omit every thread after the first page.
# Read-only; the query text is fixed here.
#
# **The path is the pull request author's text, and it reached the transcript
# verbatim (#14).** On a public pull request the author chooses the filenames,
# git permits a newline and other control characters inside one, and `jq -r`
# prints whatever arrives — so a crafted name could add lines to the listing
# `/review-copilot` reads, and that command holds `Edit` and runs unattended
# inside `/ship`. `copilot-authors.sh` already sanitises the LOCATIONS of the
# items it drops for exactly this reason; the admitted path was not given the
# same treatment.
#
# **The fix is the one `pr-locality.sh` already states in its own header** —
# each name arrives JSON-encoded, one field per line and unambiguous, and is
# printed only if it decodes to a plain path: no escape in it, path characters
# only, a `/` or a `.` in it, no `..` segment. Any other name refuses the whole
# run rather than dropping a row, because a thread list with one line withheld
# is a list `/review-copilot` would read as complete and step 6 would read as
# a clean exit.
#
# The three GitHub-supplied fields are validated too. They are not the author's
# text, which is an assumption rather than a check — and this file exists
# because an assumption of exactly that shape was wrong about the fourth.
set -euo pipefail
pr="${1:?usage: pr-review-threads.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }

refuse() {
  echo "pr-review-threads: $1" >&2
  exit 3
}

owner=$(gh repo view --json owner --jq .owner.login)
repo=$(gh repo view --json name --jq .name)
cursor=""
all_rows=""
while :; do
  resp=$(gh api graphql -f query='
  query($owner:String!,$repo:String!,$pr:Int!,$after:String){
    repository(owner:$owner,name:$repo){
      pullRequest(number:$pr){
        reviewThreads(first:100, after:$after){
          pageInfo{ hasNextPage endCursor }
          nodes{ id isResolved comments(first:1){ nodes{ databaseId path } } }
        }
      }
    }
  }' -F owner="$owner" -F repo="$repo" -F pr="$pr" ${cursor:+-F after="$cursor"})
  # Tab-separated, with the path JSON-encoded inside its field: the encoding
  # is what turns a newline in a name into the two characters `\n`, so nothing
  # a name contains can reach `read` as a record boundary.
  rows=$(jq -r '.data.repository.pullRequest.reviewThreads.nodes[] |
    [ .id,
      (.isResolved | tostring),
      (.comments.nodes[0].databaseId // "" | tostring),
      (.comments.nodes[0].path // "" | @json)
    ] | @tsv' <<<"$resp")
  page_rows=""
  while IFS=$'\t' read -r tid resolved cid encoded; do
    [ -n "$tid" ] || continue
    # **`jq` writes CRLF on Windows, and only the LAST field carries the CR.**
    # So the three validated fields pass and the path arrives as `"…"\r`,
    # which is not a JSON string — the whole run refused, on every ordinary
    # pull request, on the platform this harness is developed on. Measured
    # against PR #13 before this line existed.
    encoded="${encoded%$'\r'}"
    # A review-thread node id, in exactly the shape `pr-thread-resolve.sh`
    # accepts: a looser `[A-Za-z0-9_=-]+` printed rows such as `abc` as valid
    # that the only consumer then refused, so a thread could be listed and
    # never resolved. Raised by Copilot. The database id is digits;
    # `isResolved` is a boolean rendered by `tostring`.
    grep -Eq '^PRRT_[A-Za-z0-9_-]+$' <<<"$tid" ||
      refuse "a thread id is not the shape GraphQL returns"
    case "$resolved" in true|false) ;;
      *) refuse "a thread's resolved state is neither true nor false" ;; esac
    grep -Eq '^[0-9]+$' <<<"$cid" ||
      refuse "a thread's first comment carries no numeric database id"
    case "$encoded" in
      '"'*'"') ;;
      *) refuse "a thread's path did not arrive as a JSON string" ;;
    esac
    case "$encoded" in *\\*)
      refuse "a thread's path is not a plain path" ;; esac
    path="${encoded:1:${#encoded}-2}"
    grep -Eq '^[A-Za-z0-9_./@+()-]+$' <<<"$path" ||
      refuse "a thread's path is not a plain path"
    # A root file with no extension — `LICENSE`, `Makefile` — is a plain path,
    # and requiring a `/` or a `.` refused the whole listing over one. What
    # that requirement may have been standing in for is a leading `-`, which
    # a consumer could take as a flag, so that is refused by name. Raised by
    # Copilot.
    case "$path" in -*)
      refuse "a thread's path is not a plain path" ;; esac
    case "/$path/" in *//*|*/./*|*/../*)
      refuse "a thread's path is not a plain path" ;; esac
    page_rows="${page_rows}${page_rows:+$'\n'}${tid} ${resolved} ${cid} ${path}"
  done <<<"$rows"
  if [ -n "$page_rows" ]; then
    all_rows="${all_rows}${all_rows:+$'\n'}${page_rows}"
  fi
  [ "$(jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.hasNextPage' <<<"$resp")" = "true" ] || break
  cursor=$(jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.endCursor' <<<"$resp")
done
[ -z "$all_rows" ] || printf '%s\n' "$all_rows"
