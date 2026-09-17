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

case "$mode" in
  all|fast)
    exec dotnet test BlueprintAdmin.slnx
    ;;
  *)
    echo "usage: host-checks.sh [all|fast]" >&2
    exit 2
    ;;
esac
