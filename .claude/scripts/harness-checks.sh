#!/usr/bin/env bash
# The payload's suite, then this repository's own suites, with no free parameter
# anywhere.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"

run_py() {
  # Same probe order as run-guard.sh: `py -3.12`, then `py -3` for a Windows
  # host that only has 3.13+ registered, then python3, then python. The
  # floor is 3.12+; the exact selector is first because it is this repo's
  # pin when it exists.
  probe='import sys; sys.exit(sys.version_info < (3, 12))'
  if command -v py >/dev/null 2>&1 && py -3.12 -c "$probe" >/dev/null 2>&1; then
    py -3.12 "$@"
    return
  fi
  if command -v py >/dev/null 2>&1 && py -3 -c "$probe" >/dev/null 2>&1; then
    py -3 "$@"
    return
  fi
  if command -v python3 >/dev/null 2>&1 && python3 -c "$probe" >/dev/null 2>&1; then
    python3 "$@"
    return
  fi
  if command -v python >/dev/null 2>&1 && python -c "$probe" >/dev/null 2>&1; then
    python "$@"
    return
  fi
  echo "harness-checks.sh: no Python 3.12+ as py -3.12, py -3," \
       "python3 or python" >&2
  exit 2
}

# It takes no arguments: the worker count is the host's, capped, and a caller
# who could set it would be a second opinion about what the suite is.
run_py .claude/scripts/shard-harness-suite.py

# The roots the payload's runner does not know, each serial. `unittest discover`
# exits 5 on no tests from 3.12 on, so an empty discovery refuses this check.
# The `harness` job in ci.yml runs the same list, and
# test_harness_helpers_admin.py holds the two together.
run_py -m unittest discover -s .github/locality-gate -p 'test*.py'
run_py -m unittest discover -s .github/commit-gate -p 'test*.py'
run_py -m unittest discover -s .github/pin-gate -p 'test*.py'
run_py -m unittest discover -s .github/issue-form-gate -p 'test*.py'
run_py -m unittest discover -s tools/token-usage -p 'test_*.py'
