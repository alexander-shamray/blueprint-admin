#!/usr/bin/env bash
# `dotnet test` for /review-branch and /ship, with no free parameter anywhere.
#
# A trailing argument would choose which project to build, and an MSBuild
# property chooses a file to import. So the solution and the flags are fixed.
# Admin.Host.Tests do not need Docker; FakePlatform is in-process.
set -euo pipefail

mode="${1:-all}"
root="$(git rev-parse --show-toplevel)"
cd "$root"

# One line of how long this run took and how it ended, outside every checkout
# (docs/harness-boundaries.md, *The suite's shard runner*). The exit stays the
# tests': a record that cannot be made is said and never changes it.
record_run() {
  code=$?
  for py in "py -3.12" "py -3" python3 python; do
    if $py -c 'import sys; sys.exit(sys.version_info < (3, 12))' >/dev/null 2>&1; then
      $py "$root/.claude/scripts/harness_timings.py" record --script host-checks.sh \
        --mode "$mode" --wall "$SECONDS" --exit "$code" || true
      exit "$code"
    fi
  done
  echo "host-checks.sh: no Python 3.12+, so the run's time was not recorded" >&2
  exit "$code"
}
trap record_run EXIT

case "$mode" in
  all|fast)
    dotnet test BlueprintAdmin.slnx
    ;;
  *)
    trap - EXIT
    echo "usage: host-checks.sh [all|fast]" >&2
    exit 2
    ;;
esac
