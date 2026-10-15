#!/usr/bin/env bash
# Run the index search hint, `index-hint-run.sh <event> index-search-hint.py`,
# under a bound shorter than the harness's own, and leave with 0 whatever
# happened.
#
# **Not `run-guard.sh`, although that is the launcher the guards use.** It
# leaves with 2 on every failure, because 2 is what blocks a `PreToolUse` call;
# a hint that could not start must not stop a search, so this exits 0 on every
# path. A hook the harness kills has its output discarded, so a stalled or
# missing interpreter would leave no sign the hint had not run: past the bound,
# or on any non-zero exit, this prints a line saying so instead.
#
# **A closed set of hook names, like every launcher here.** Its only caller is
# the wiring in `.claude/settings.json`, which names the one file below; a
# launcher taking any path would be a way to run an arbitrary script through
# the hook wiring.
#
# **The interpreter is `py -3` where the Windows launcher exists and `python3`
# or `python` where it does not**, chosen once and run once, never a fallback
# chain: a chain re-runs the hook when the first attempt exits non-zero, and
# a hook that printed a refusal would print it twice. `-P` keeps a checkout-root
# `json.py` off the import path.
set -u
HINT_BOUND_SECONDS="${HINT_BOUND_SECONDS:-10}"
event="${1:-}"
hook="${2:-}"

note() {
  printf '{"hookSpecificOutput":{"hookEventName":"%s","additionalContext":"The code-index hint hook %s did not run (%s), so no hint here says nothing about whether one applies."}}\n' \
    "${event:-PreToolUse}" "${hook:-unnamed}" "$1"
}

[ "$event" = "PreToolUse" ] && [ "$hook" = "index-search-hint.py" ] ||
  { note "this launcher runs only index-search-hint.py under PreToolUse"; exit 0; }

if command -v py >/dev/null 2>&1; then
  python=(py -3)
elif command -v python3 >/dev/null 2>&1; then
  python=(python3)
else
  python=(python)
fi
bound=()
command -v timeout >/dev/null 2>&1 && bound=(timeout "$HINT_BOUND_SECONDS")
export HINT_HOOK="$hook"

# A file, not a substitution: a launcher's orphaned child would hold a pipe
# open and keep this waiting.
out="$(mktemp)" || { note "no temporary file"; exit 0; }
trap 'rm -f "$out"' EXIT
rc=0
${bound[@]+"${bound[@]}"} "${python[@]}" -P -c \
  "import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR'] + '/.claude/hooks/' + os.environ['HINT_HOOK'], run_name='__main__')" \
  >"$out" || rc=$?
if [ "$rc" -eq 0 ]; then
  cat "$out"
else
  note "exit $rc after at most $HINT_BOUND_SECONDS s"
fi
exit 0
