#!/usr/bin/env bash
# The harness and locality-gate suites, with no free parameter anywhere.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"

run_py() {
  if command -v py >/dev/null 2>&1 && py -3.12 -c 'import sys; sys.exit(sys.version_info < (3, 12))' >/dev/null 2>&1; then
    py -3.12 "$@"
    return
  fi
  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys; sys.exit(sys.version_info < (3, 12))' >/dev/null 2>&1; then
    python3 "$@"
    return
  fi
  if command -v python >/dev/null 2>&1 && python -c 'import sys; sys.exit(sys.version_info < (3, 12))' >/dev/null 2>&1; then
    python "$@"
    return
  fi
  echo "harness-checks.sh: no Python 3.12+" >&2
  exit 2
}

run_py -m unittest discover -s .claude/scripts -p 'test_*.py'
run_py -m unittest discover -s .github/locality-gate
