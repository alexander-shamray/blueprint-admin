#!/usr/bin/env bash
# Feed pr.md's closure check — a PR's number, url, body, commits, GitHub's
# own closing-issue parse and the head oid, as JSON on stdout. Read-only,
# fixed field set.
#
# **The consumer is a person here, not a gate.** In the repository this came
# from, `.github/closure-gate/closure_gate.py` read this on every push and
# every description edit; that workflow was not ported, so what reads this
# output is whoever runs `/pr`. Saying otherwise would make a missing control
# look enforced, which is the one way a missing control does real damage.
#
# **Exists so that pr.md need not hold `Bash(gh pr view:*)` (#56).** This was
# the third command carrying that grant, after review-copilot.md and ship.md,
# and it was found by the test whose subject is every command's frontmatter
# rather than by reading the two files the issue named — which is the whole
# argument for writing the gate's test against the surface instead of against
# the instance.
#
# The field set is exactly what that comparison needs, and it is fixed here for
# the reason every helper in this directory fixes its endpoint: a caller that
# chooses its own fields can choose `reviews`, which is the unfiltered route
# the three feed helpers exist to close.
#
# `body` and `commits` cross here and that is intentional — both are the
# repository's own text, and the consumer is a parser rather than a model.
set -euo pipefail
pr="${1:?usage: pr-closure-input.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh pr view "$pr" --json number,url,body,commits,closingIssuesReferences,headRefOid
