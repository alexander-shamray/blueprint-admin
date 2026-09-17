#!/usr/bin/env bash
# Every issue a sweep de-duplicates against, with a FIXED field set.
#
# **This is the listing half of the control `gh-issue-text.sh` closed one grant
# along.** #150 dropped `author` from the listing *line* and both sweeps wrote
# that the field's absence is what stops the suppression decision being taken in
# passing. Review then found `Bash(gh issue view:*)` returning the same field,
# and that was closed with a fixed-field helper — citing #56, that a helper
# which fixes its field set does not bind a caller who still holds the raw grant
# and can choose fields.
#
# `Bash(gh issue list:*)` was left in place through both of those rounds, and it
# is a prefix: `gh issue list --json author` and `--json body` are matches of
# it. Measured, not argued — the first returns `{"author":{"login":...}}` for
# every issue in the repository. So the session could still read the one field
# the helpers exist to withhold, and dump every issue body, without touching
# either. The instruction line naming four fields was a rule a reader follows.
#
# That is the same defect three times in one branch, each time one grant along
# from the one just closed. The fix is the shape `pr-for-branch.sh` already
# uses: the field set is spelled here and the caller chooses nothing.
#
# `labels` stays because the sweeps apply and read them; `author` and `body` do
# not, because authorship is `gh-issue-suppresses.sh`'s to decide — in code,
# reduced to an exit status — and body text is `gh-issue-text.sh`'s to hand over
# one issue at a time rather than a thousand at once.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: gh-issue-list.sh   (no arguments)" >&2; exit 2; }

# `--state all`, because the sweeps de-duplicate against closed issues too: a
# finding already filed and fixed must not be re-filed. `--limit 1000`, because
# the default 30 hides older issues and a de-duplication gate that cannot see an
# issue reports a duplicate as new.
#
# **And 1000 is still a bound, which is what #17 and #22 are about.** Past it
# the older issues are omitted and a sweep files a duplicate while reporting a
# clean de-duplication — the fail-open shape those sweeps exist to refuse,
# occurring in the gate itself. `gh issue list` has no `--paginate`, so the cap
# is detected instead: a response holding exactly the limit is refused rather
# than returned. This repository has far fewer issues than that, so the path is
# latent — which is the reason to close it now rather than when it is not.
LIMIT=1000
issues=$(gh issue list --state all --limit "$LIMIT" --json number,title,state,labels)
[ "$(jq 'length' <<<"$issues")" -lt "$LIMIT" ] ||
  { echo "gh issue list returned exactly $LIMIT issues, so the listing is truncated and de-duplicating against it would report a duplicate as new" >&2; exit 3; }
printf '%s
' "$issues"
