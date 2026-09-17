#!/usr/bin/env bash
# The local gate for /review-branch and /ship, with no free parameter anywhere.
#
# It replaces `Bash(npm run:*)` and `Bash(npx:*)`, both of which are grants far
# wider than the operation the commands name. `npm run <name>` executes whatever
# `package.json` says under that name — including a script a session may
# legitimately have just written — and `npm test -- <args>` forwards its tail to
# Vitest, which takes `--config <path>` and EXECUTES that file to load it. Either
# one turns "run the tests" into "run this". So the script names and the flag
# sets are fixed here, and the only variable is one word out of a closed set.
#
# **This closes the executor and not the manifest, and the gap is wider than an
# earlier draft of this header admitted.** `package.json` is still a file the
# session can edit, and every mode below runs whatever its `scripts` block
# currently says; the same holds for the config files those scripts read
# (`angular.json`, `eslint.config.js`, the Vitest config) and for
# `node_modules/.bin`. review-branch.md's `disallowed-tools` denies all of them,
# and this header used to point there as if that settled it.
#
# **It does not, and Copilot was right to say so against PR #13.** A deny stops
# THIS agent from editing those files. It cannot stop a BRANCH from arriving
# with them already edited — and /review-branch's whole subject is a branch
# somebody else wrote. So on an untrusted branch, `npm ci` alone runs any
# `preinstall`/`postinstall` in the manifest or in any dependency the lockfile
# names, and every mode below loads configuration that is executed in order to
# be read. The deny protects the file; nothing here protects the host.
#
# **What actually stands is the trust boundary, not this script.** These modes
# run on the developer's own machine against a branch the developer is
# reviewing, which is the same act as opening the repository in an editor with
# a language server — and the external reviewer, the one case where the branch
# is genuinely untrusted, gets no Node toolchain at all and cannot install one
# (see .claude/sandbox/Dockerfile). Running these checks on a fork's branch,
# unattended, is the case this does not cover; do that in a disposable
# environment with no credentials, or not at all.
#
# `e2e` is deliberately NOT one of the modes. Playwright needs the Host under
# FakePlatform; a mode that silently passed because nothing was listening
# would be worse than no mode at all. E2E is CI's `smoke` job —
# `docs/testing.md` owns what a local run needs.
set -euo pipefail

mode="${1:-all}"
root="$(git rev-parse --show-toplevel)"
cd "$root/src/Admin.Web"

case "$mode" in
  all)
    # The web job of .github/workflows/ci.yml, in its order. Lint first because
    # it is the cheapest and the most often red; build last because it is the
    # slowest and a type error has usually already surfaced in the tests.
    npm run lint
    npm test
    exec npm run build
    ;;
  fast)
    # Everything but the production build. `ng build` is the long pole, and it
    # re-typechecks what `ng test` has already compiled, so this is the mode for
    # a loop that runs on every edit.
    npm run lint
    exec npm test
    ;;
  lint)
    exec npm run lint
    ;;
  test)
    exec npm test
    ;;
  build)
    exec npm run build
    ;;
  *)
    echo "usage: npm-checks.sh [all|fast|lint|test|build]" >&2
    exit 2
    ;;
esac
