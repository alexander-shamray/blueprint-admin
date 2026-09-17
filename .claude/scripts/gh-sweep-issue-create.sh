#!/usr/bin/env bash
# File one issue on THIS repository FROM A SWEEP: the kind and severity from
# fixed vocabularies on the command line, the title and body on stdin, and the
# provenance line a constant this caller cannot choose.
#
# **This exists so that a sweep cannot name its own provenance (#19).** The
# route was the third argument of `gh-issue-create.sh`, and both sweeps held a
# prefix grant on that script — so the model chose all three arguments,
# including the sentence asserting that a second read-only auditor verified the
# finding at filing. That is the claim a triager reads to decide whether
# anybody checked, and it was the one argument the closed-set reasoning behind
# `kind` and `severity` never reached.
#
# The trailer this route requires is:
#
#   Filed by an authorised sweep and verified at filing by a second read-only
#   auditor.
#
# and there is deliberately no spelling for "filed by a sweep whose auditor did
# not run" — a sweep that cannot verify a finding does not file it.
#
# Everything else is `gh-issue-filing.sh`'s and argued in full there. Both
# sweeps grant this script and deny `gh-issue-create.sh` by name, so neither
# route is reachable from the other.
set -euo pipefail

[ "$#" -eq 2 ] || {
  echo "usage: gh-sweep-issue-create.sh <security|bug>" \
       "<critical|high|medium|low>" \
       "< title, blank line, body ending in the sweep trailer" >&2
  exit 2
}

source "$(dirname "${BASH_SOURCE[0]}")/gh-issue-filing.sh"
gh_issue_file "$1" "$2" sweep
