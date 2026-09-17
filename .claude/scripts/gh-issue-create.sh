#!/usr/bin/env bash
# File one issue on THIS repository BY HAND: the kind and severity from fixed
# vocabularies on the command line, the title and body on stdin, and the
# provenance line a constant.
#
# **The route used to be the third argument, and #19 is why it is not.** It was
# the one argument that escaped the reasoning `gh-issue-filing.sh` makes about
# the other two: both sweeps held a prefix grant on this script, so the model
# chose all three — including which provenance claim the issue carried. The
# dangerous direction is a hand-filed or injected finding claiming `sweep`,
# which asserts a verification that did not happen.
#
# So there are two entry points and neither can name the other's trailer.
# This one is `hand`; `gh-sweep-issue-create.sh` is `sweep`. Everything else —
# the closed vocabularies, the stdin title, the truncation detector, the
# resolved repository, the MSYS title fix — is `gh-issue-filing.sh`'s and is
# argued in full there.
set -euo pipefail

[ "$#" -eq 2 ] || {
  echo "usage: gh-issue-create.sh <security|bug> <critical|high|medium|low>" \
       "< title, blank line, body ending in the hand-filed trailer" >&2
  exit 2
}

source "$(dirname "${BASH_SOURCE[0]}")/gh-issue-filing.sh"
gh_issue_file "$1" "$2" hand
