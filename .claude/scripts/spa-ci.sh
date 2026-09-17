#!/usr/bin/env bash
# `npm ci` for the SPA, with no free parameter and a fixed working directory.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root/src/Admin.Web"
exec npm ci
