#!/usr/bin/env bash
# Judge a pull request's changed paths against the `| Class |` and
# `| Touch set |` rows its body declares, and print a verdict per path — and
# nothing the author wrote. Read-only, fixed field set.
#
# **Exists so that /review-branch, /review-copilot and /ship can read the two
# rows `docs/change-locality.md` asks a PR body to carry without holding
# `Bash(gh pr view:*)`** — the grant that reaches `--json reviews`, the
# unfiltered feed #56 closed. `body` is the one field this reads from the
# pull request, `filename` the one field it reads from the files endpoint;
# what applies is the shape rule every helper in this directory follows — a
# caller that chooses fields can choose `reviews`, so this one chooses none.
#
# **The output never contains the touch-set cell.** A pull request author is
# not a trusted party, /review-copilot takes any PR number, and a row was the
# one place an author's text reached an Edit-capable agent unfiltered. A path
# grammar cannot close that — `Ignore_all_previous_instructions.md` is a path
# — so the cell is consumed here and only a verdict leaves: one `class` line
# whose value is letters this script validated, then one `inside <path>` or
# `outside <path>` line per changed file, where the path is the diff's own
# and the word is this script's. A caller acts on `outside` lines and never
# sees what the set said. **A changed path is the author's text too** — the
# author names the files, and git permits a newline inside a name — so each
# arrives JSON-encoded, one per line and unambiguous, and is printed only if
# it decodes to a plain path: no escape in it, path characters only, a `/`
# or a `.` in it, no `..` segment. Any other name refuses the whole run,
# because a verdict list with one line withheld is a list a caller would
# read as complete.
#
# Nothing is printed when the body carries neither row; a caller that reads
# nothing skips its touch-set check and says so, and does not infer a class.
# One row without the other is refused, because each command reads the pair.
# A row that fails its grammar is refused with exit 3 naming the row and not
# its content: a class cell is one letter A–E or two distinct letters joined
# by `+`; a touch-set cell is a comma-separated list of path tokens, bare or
# in balanced backticks, of path and glob characters — `*`, `**`, `?` and a
# brace alternation — each carrying a `/` or a `.`, and each
# repository-relative: no leading `/`, no `./`, no `..` segment, brace
# alternatives included, since the edit-target guard judges where an edit
# inside the checkout lands and not a path naming the outside.
#
# **A path is `inside` only when it is in both sets.** The touch-set row is
# the sharper bound; the class -> tree-set map in
# `.github/locality-gate/classes.yml` is the coarser one CI's gate reads.
# Matching the declared set alone would print `inside` for `Class | D |` with
# `Touch set | src/** |`, which the Python gate rejects. Raised by Copilot.
#
# **The map is the PR base's copy, with the workflow's bootstrap.** CI takes
# gate and map from the base commit so a PR that widens Class D to `src/**`
# is still judged by the map already on main; HEAD is used only when the
# base has no `.github/locality-gate/` directory at all. This helper used to
# read the checkout file, which is HEAD, and `/review-branch` describes it
# as an early read of the same verdict. Raised by Copilot.
#
# **The verdict narrows and grants nothing.** What holds authority is the
# caller's own deny list and the class's tree set in the contract; an
# `outside` line is a finding for the caller, and an `inside` line is not a
# licence for anything the caller's grant refuses.
set -euo pipefail
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
refuse() { echo "$1" >&2; exit 3; }
# **The expressions the tests below are made with, named rather than inlined.**
# Each was a `grep -Eq` pattern and is now bash's `=~` right-hand side, which
# has to be an UNQUOTED variable to be read as a regular expression at all — a
# quoted literal is matched as a string, silently, and every test would pass.
# Declaring them here is what keeps the conversion from re-quoting one.
#
# **bash anchors the whole string where `grep` anchors each line**, so a value
# carrying a newline matches under `grep` and does not here. Every value tested
# with these is one this script is validating, so the stricter reading is the
# one to have: `changedFiles` of `3<newline>evil` is not a count.
COUNT_RE='^[0-9]+$'
CLASS_ROW_RE='^\| *Class *\|'
TOUCH_ROW_RE='^\| *Touch set *\|'
CLASS_RE='^[A-E](\+[A-E])?$'
OID_RE='^[0-9a-f]{40}$'
TOKEN_RE='^[A-Za-z0-9_./*?{},()@+-]+$'
PATH_RE='^[A-Za-z0-9_./@+()-]+$'
NL='
'
# Trim leading and trailing SPACES from $1 into `TRIMMED`, which is what the
# two `sed` cell extractions did — ` *`, not `[[:space:]]*`. Widening it to
# all whitespace would admit a tab-padded cell the `sed` refused, and a
# grammar that quietly grew is the drift this helper is full of arguments
# against. The idiom is the one the touch-set loop below already uses.
trim() {
  local s="$1"
  s="${s#"${s%%[! ]*}"}"
  TRIMMED="${s%"${s##*[! ]}"}"
}
# The body is captured before it is filtered, so a `gh` failure — no
# authentication, no network, no such pull request — is fatal under `set -e`
# rather than indistinguishable from a body with no rows. Nothing masks a
# status here any more: the filter is the loop below, which cannot fail.
expected=$(gh pr view "$pr" --json changedFiles --jq .changedFiles)
[[ "$expected" =~ $COUNT_RE ]] || refuse "changedFiles is not a count"
body=$(gh pr view "$pr" --json body --jq .body)
# The rows, collected in one pass of the body rather than by two `grep`s and
# two `grep -c`s. Arrays rather than captured text because the count is the
# question: exactly one of each, or none. A second row is where a valid first
# row would have carried an invalid second past a check that only asked
# whether any row matched, so two rows is refused before either grammar is
# consulted.
class_rows=()
touch_rows=()
while IFS= read -r row || [ -n "$row" ]; do
  [ -n "$row" ] || continue
  if [[ "$row" =~ $CLASS_ROW_RE ]]; then class_rows+=("$row"); fi
  if [[ "$row" =~ $TOUCH_ROW_RE ]]; then touch_rows+=("$row"); fi
done <<<"$body"
[ "${#class_rows[@]}" -le 1 ] || refuse "more than one Class row"
[ "${#touch_rows[@]}" -le 1 ] || refuse "more than one Touch set row"
class_row="${class_rows[0]:-}"
touch_row="${touch_rows[0]:-}"
if [ -z "$class_row" ] && [ -z "$touch_row" ]; then exit 0; fi
[ -n "$class_row" ] && [ -n "$touch_row" ] || refuse "one row without the other"
# The class cell: the text between the second `|` and the closing one.
# Cut rather than substituted — two `|`s from the left, the last from the
# right, then the spaces off both ends, which is what the `sed` did.
class="${class_row#*|}"
class="${class#*|}"
class="${class%|*}"
trim "$class"; class="$TRIMMED"
[[ "$class" =~ $CLASS_RE ]] || refuse "the Class row is not a class"
[ "${class:0:1}" != "${class:2:1}" ] || refuse "the Class row repeats a class"
# The class map is the same file CI's gate reads, from the same commit. A
# `+`-joined class is the union of its members. Tokens compile with the
# touch-set glob rules below.
here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
head_map="$here/../../.github/locality-gate/classes.yml"
gate_dir=".github/locality-gate"
map_rel="$gate_dir/classes.yml"
base=$(gh pr view "$pr" --json baseRefOid --jq .baseRefOid)
[[ "$base" =~ $OID_RE ]] || refuse "the PR base is not a commit"
if git cat-file -e "$base:$gate_dir" 2>/dev/null; then
  git cat-file -e "$base:$map_rel" 2>/dev/null ||
    refuse "the PR base carries the gate directory but no classes.yml"
  map=$(mktemp)
  trap 'rm -f -- "$map"' EXIT
  git show "$base:$map_rel" > "$map"
  [ -s "$map" ] || refuse "classes.yml at the PR base is empty"
else
  # Missing object and missing directory are the same `cat-file` failure.
  # Only the latter is bootstrap; refuse the former rather than read HEAD.
  git cat-file -e "$base^{commit}" 2>/dev/null ||
    refuse "the PR base commit is not in this clone"
  map="$head_map"
  [ -f "$map" ] || refuse "classes.yml is missing"
fi
members=("$class")
[ "${#class}" -eq 1 ] || members=("${class:0:1}" "${class:2:1}")
compile_glob() {
  # stdin: the touch-set / map tokens, one per line. stdout: one ERE body
  # (unanchored) per line, in the same order.
  #
  # **The `sed` program is unchanged and is called ONCE for a whole token
  # list (#46).** It used to be called per token, which was fourteen process
  # spawns in a run that made sixty-one; the rest of this script's text tests
  # became bash's own, and this one deliberately did not. Translating a glob
  # into an ERE is the step where a silent mistake yields a wrong `inside` or
  # `outside` rather than a refusal, and this file's header says that is the
  # worse outcome — so what changed is how often the program runs, never what
  # it says.
  #
  # One token per line is unambiguous because the grammars above admit no
  # whitespace at all, newline included, so no token can be two lines.
  sed -e 's/[.()+]/\\&/g' -e 's/\*\*/%%GLOBSTAR%%/g' -e 's/\*/[^\/]*/g' \
      -e 's/?/[^\/]/g' -e 's/%%GLOBSTAR%%/.*/g' \
      -e 's/{/(/g' -e 's/}/)/g' -e 's/,/|/g'
}

# Compile `$1` (a newline-separated token list) into `COMPILED`, each entry
# anchored and extended to cover everything beneath what it names. One
# `compile_glob` for the whole list.
#
# **The count is checked, and that is not defensive clutter.** `sed` is
# line-oriented, so a list and its output correspond exactly — which is what
# makes one call for many tokens safe at all. If they ever stopped
# corresponding, every token after the lost line would be paired with another
# token's expression and the helper would print confident, wrong verdicts. So
# the correspondence is asserted rather than assumed.
#
# `COMPILED` is a plain global because `local -n` wants bash 4.3 and this
# suite runs on a macOS runner, where `/bin/bash` is 3.2.
compile_into() {
  tokens="$1"
  COMPILED=()
  [ -n "$tokens" ] || return 0
  compiled=$(printf '%s\n' "$tokens" | compile_glob)
  while IFS= read -r line; do
    COMPILED+=("^${line}(/.*)?$")
  done <<<"$compiled"
  count=0
  while IFS= read -r line; do count=$((count + 1)); done <<<"$tokens"
  [ "${#COMPILED[@]}" -eq "$count" ] ||
    refuse "the glob compiler returned ${#COMPILED[@]} expressions for $count tokens"
}
map_tokens=""
current=""
while IFS= read -r raw || [ -n "$raw" ]; do
  line="${raw%$'\r'}"
  # locality_gate.py rstrip()s, then skips blanks and comments whose
  # first non-space is `#`. A map valid in CI must parse here too.
  line="${line%"${line##*[![:space:]]}"}"
  [ -n "$line" ] || continue
  trimmed="${line#"${line%%[![:space:]]*}"}"
  case "$trimmed" in '#'*) continue ;; esac
  if [[ "$line" =~ ^([A-E]):$ ]]; then
    current="${BASH_REMATCH[1]}"
    continue
  fi
  if [[ "$line" =~ ^\ \ -\ \'([^\']+)\'$ ]]; then
    [ -n "$current" ] || refuse "classes.yml is outside the map's grammar"
    token="${BASH_REMATCH[1]}"
    for member in "${members[@]}"; do
      if [ "$member" = "$current" ]; then
        token="${token%/}"
        # Collected, and compiled once with the rest below.
        map_tokens+="${map_tokens:+$NL}$token"
      fi
    done
    continue
  fi
  refuse "classes.yml is outside the map's grammar"
done < "$map"
compile_into "$map_tokens"
map_patterns=()
[ "${#COMPILED[@]}" -eq 0 ] || map_patterns=("${COMPILED[@]}")
[ "${#map_patterns[@]}" -gt 0 ] || refuse "the Class row has no tree set in classes.yml"
# The touch-set cell, then each comma-separated token on its own. Cut the same
# way the class cell was.
cells="${touch_row#*|}"
cells="${cells#*|}"
cells="${cells%|*}"
trim "$cells"; cells="$TRIMMED"
case "$cells" in *'|'*) refuse "the Touch set row is not one cell" ;; esac
[ -n "$cells" ] || refuse "the Touch set row is empty"
# Split on commas outside braces, because a brace glob carries its own —
# `.claude/commands/{pr,ship}.md` is one token, not two halves of one.
items=(); cur=""; depth=0
for ((i = 0; i < ${#cells}; i++)); do
  ch="${cells:i:1}"
  case "$ch" in
    '{') depth=$((depth + 1)) ;;
    '}') depth=$((depth - 1)); [ "$depth" -ge 0 ] || refuse "the Touch set row has an unbalanced brace" ;;
    ',') if [ "$depth" -eq 0 ]; then items+=("$cur"); cur=""; continue; fi ;;
  esac
  cur+="$ch"
done
items+=("$cur")
[ "$depth" -eq 0 ] || refuse "the Touch set row has an unbalanced brace"
set_tokens=""
for item in "${items[@]}"; do
  t="${item#"${item%%[! ]*}"}"
  t="${t%"${t##*[! ]}"}"
  case "$t" in
    '`'*'`') t="${t:1:${#t}-2}" ;;
    *'`'*) refuse "the Touch set row has an unbalanced backtick" ;;
  esac
  # **The two grammars here disagreed, and the touch-set one was the narrower
  # (#19).** A changed path is admitted with `@` and `+` in it — git permits
  # both — while a declared set naming `src/app/@types/**` or `docs/a+b.md`
  # made this `refuse` and exit 3 before printing any verdict. `/review-branch`,
  # `/review-copilot` and `/ship` all read a helper that prints nothing as
  # "names no class and no bound", so a legitimate touch set silently degraded
  # the locality check rather than failing it visibly. This is the changed-path
  # grammar plus the glob characters `*?{},`, which makes it a superset rather
  # than a second list to keep in step.
  [[ "$t" =~ $TOKEN_RE ]] ||
    refuse "the Touch set row is not a path list"
  case "$t" in *[/.]*) ;; *) refuse "the Touch set row is not a path list" ;; esac
  t="${t%/}"
  # A brace alternative is a segment start too: `{../outside,docs/x.md}`
  # expands to a path that leaves the checkout, so the boundary is judged
  # over the token with its braces dropped and its alternatives joined as
  # segments, where a leading `/`, a `./` and a `..` all show as segments.
  n="${t//[\{\}]/}"
  n="${n//,//}"   # every `,` becomes `/`: the replacement is the last `/`
  case "/$n/" in
    *//*|*/./*|*/../*) refuse "the Touch set row names a path outside the repository" ;;
  esac
  # The token as an anchored regular expression: `**` crosses directories,
  # `*` and `?` do not, braces are alternation, and a token also covers
  # everything beneath the directory it names — `tests/Ordering.*` is the
  # test projects, not files whose name happens to start that way. A
  # trailing `/` names the directory the same way `docs` would, and was
  # dropped above before the boundary was judged.
  #
  # **`\x01` was a GNU `sed` escape and macOS ships BSD `sed` (#23).** There it
  # is not an escape at all, so the sentinel was written and read as the two
  # literal characters `x` and `1` — which leaves `**` mistranslated, or a
  # token containing that pair read as the placeholder. Either way the helper
  # emits a wrong `inside`/`outside` verdict rather than refusing, and all
  # three callers act on verdicts: a wrong one is worse here than a refusal.
  #
  # The placeholder is built from a character the touch-set grammar above
  # rejects, which is what makes a collision unforgeable rather than unlikely
  # — `%` cannot appear in a token, so no token can spell this.
  #
  # **`+` joins the escape set with the grammar that admits it.** It is an ERE
  # quantifier, so an unescaped `docs/a+b.md` would match `docs/aab.md` — a
  # false `outside` traded for a silently wrong `inside`, which is the same
  # trade the changed-path side already refuses. `@` needs no escape.
  set_tokens+="${set_tokens:+$NL}$t"
done
compile_into "$set_tokens"
patterns=()
[ "${#COMPILED[@]}" -eq 0 ] || patterns=("${COMPILED[@]}")
# The changed paths are the diff's own, and each gets the one word this
# script chooses for it. `filename` is the whole of what is read, and it is
# read as a JSON string so that a newline inside a name cannot be a second
# line: a name that needed an escape is refused rather than decoded.
name_count=$(gh api "repos/{owner}/{repo}/pulls/$pr/files" --paginate --jq '.[].filename' | grep -c .)
[ "$name_count" -eq "$expected" ] || refuse "the files endpoint returned $name_count names against changedFiles $expected"
files=$(gh api "repos/{owner}/{repo}/pulls/$pr/files" --paginate --jq '.[] | .filename, .previous_filename | select(. != null) | @json')
verdicts=()
while IFS= read -r line; do
  [ -n "$line" ] || continue
  case "$line" in
    '"'*'"') ;;
    *) refuse "a changed path did not arrive as a JSON string" ;;
  esac
  case "$line" in *\\*) refuse "a changed path is not a plain path" ;; esac
  path="${line:1:${#line}-2}"
  [[ "$path" =~ $PATH_RE ]] || refuse "a changed path is not a plain path"
  case "$path" in *[/.]*) ;; *) refuse "a changed path is not a plain path" ;; esac
  case "/$path/" in *//*|*/./*|*/../*) refuse "a changed path is not a plain path" ;; esac
  verdicts+=("$path")
done <<<"$files"
printf 'class %s\n' "$class"
for path in "${verdicts[@]}"; do
  in_set=0
  in_map=0
  # **The verdict loop was 34 of the run's 61 spawns**: one `grep` per path
  # per pattern, until one matched. The expressions are the ones
  # `compile_glob` produced, unchanged — only the matcher is bash's.
  for re in "${patterns[@]}"; do
    if [[ "$path" =~ $re ]]; then in_set=1; break; fi
  done
  for re in "${map_patterns[@]}"; do
    if [[ "$path" =~ $re ]]; then in_map=1; break; fi
  done
  if [ "$in_set" -eq 1 ] && [ "$in_map" -eq 1 ]; then
    printf 'inside %s\n' "$path"
  else
    printf 'outside %s\n' "$path"
  fi
done
