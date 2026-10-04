#!/usr/bin/env sh
# Point a locate, explain, references, impact or named-symbol question at the
# code index.
#
# **Run from `UserPromptSubmit` in `.claude/settings.json`, because keeping the
# index current never got it read.** `refresh-index.sh` refreshes it, and
# `CLAUDE.md` and the skill's `description` both say to use it; neither was
# enough on its own. So a prompt that reads as one of those five questions gets
# one line of `additionalContext`: load the `codebase-index` skill, the
# subcommand its route table gives that question, and its session-tag rule.
# Every other prompt gets nothing, and so does every slash command but two:
# `/ship` and `/branch` carry a task in their arguments, and are how work here
# starts, so their arguments are read as a prompt would be. `/commit`, `/clear`
# and a plain edit request pay one `awk` and hear nothing.
#
# **The hint asks for the skill, not only the command**, because the skill's
# `allowed-tools` is what approves `codebase-index …` wherever no command
# grants it — `/ship` and `/review-branch` grant the subcommands they name —
# and it approves only while the skill is loaded; `settings.json` allows no
# such command. The CLI is named bare, as the route table and `allowed-tools`
# spell it, never through
# the `cbx` wrapper, which neither of them approves.
#
# **Not through `run-guard.sh`, although that is the launcher the guards use.**
# That launcher leaves with 2 on every failure, because 2 is what blocks a
# `PreToolUse` call; under `UserPromptSubmit` 2 blocks the PROMPT and erases
# it. A hint must fail open, so this runs under plain `sh`, as the refresh hook
# does, and leaves with 0 whatever `awk` did — an `awk` that is missing or
# fails prints nothing, and nothing is the answer for every prompt this does
# not recognise anyway.
#
# **Read-only**: no index work, no spawn but the one `awk`, no file written.
# Only the `prompt` string is matched, never the payload's paths, and it is
# decoded from its quote, backslash and whitespace escapes; its first 4096
# characters are enough for a question and keep the decode linear on every
# `awk`, where a pasted log appended one character at a time is not.

awk '
function emit(kind, command) {
  printf "{\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":\"%s\"}}\n", \
    "This reads as a " kind " question, so load the codebase-index skill and query the index before any Grep or Read of the tree, starting with " \
    command \
    ". Pick one session tag for this conversation and pass it to every search and explain; start a new one after a clear or compaction, and never hand it to a subagent. Read only the recommended line ranges, and treat an empty refs or impact result as inconclusive."
}

# The `prompt` value, decoded. A JSON string holds no raw quote, so the first
# unescaped `"` after the opening one ends it, and `"prompt"` with a raw quote
# on both sides can only be the key: inside a string that quote is `\"`. A
# string still open at the end of the payload is not a prompt; one still open
# at the cap is, as far as it goes.
function prompt_of(doc,    at, i, c, n, out, cap) {
  if (!match(doc, /"prompt"[ \t\r\n]*:[ \t\r\n]*"/)) return ""
  at = RSTART + RLENGTH
  n = length(doc)
  cap = 4096
  out = ""
  for (i = at; i <= n; i++) {
    if (i - at >= cap) return out
    c = substr(doc, i, 1)
    if (c == "\"") return out
    if (c == "\\") {
      i++
      c = substr(doc, i, 1)
      if (c == "n" || c == "r" || c == "t") c = " "
    }
    out = out c
  }
  return ""
}

{ doc = doc $0 "\n" }

END {
  p = tolower(prompt_of(doc))
  sub(/^[ \t\r\n]+/, "", p)
  if (p == "") exit 0
  # Of the slash commands, only the two that take a task have it read.
  if (substr(p, 1, 1) == "/") {
    if (p !~ /^\/(ship|branch)([ \t]|$)/) exit 0
    sub(/^\/(ship|branch)/, "", p)
  }
  # A space at each end stands in for the word boundary awk has no escape for,
  # so "somewhere is" is not "where is" and a question may end on its verb.
  p = " " p " "
  b = "[^a-z0-9_]"
  if (p ~ (b "(what (breaks|depends)|impact of)" b)) {
    emit("change-impact", "`codebase-index impact \\\"X\\\" --json`")
  } else if (p ~ (b "(who calls|what (calls|uses))" b)) {
    emit("references", "`codebase-index refs \\\"X\\\" --json`")
  # A sentence end stops `how does … work`; a dot inside a name does not.
  } else if (p ~ (b "how does ([^.?!]|[.][^ .?!])* work(s|ing)?" b)) {
    emit("how-it-works", "`codebase-index explain \\\"X\\\" --session <tag> --json`")
  } else if (p ~ (b "find (the )?(class|method|handler)(es|s)?" b)) {
    emit("named-symbol", "`codebase-index symbol \\\"X\\\" --json`")
  } else if (p ~ (b "where (is|are|does)" b)) {
    emit("locate", "`codebase-index search \\\"X\\\" --limit 5 --session <tag> --json`")
  }
}
' 2>/dev/null
exit 0
