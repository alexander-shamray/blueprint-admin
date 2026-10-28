#!/usr/bin/env bash
# Cut a sweep's worktree into slices of at most BUDGET bytes, one list per slice
# beside the worktree, and print a summary; /bug-sweep and /security-sweep.
# The row partition is the sweeps' and lives here, so a tracked path no row owns
# refuses the run rather than going unswept.
set -euo pipefail
usage="usage: sweep-slices.sh <bug|security> <worktree> [full]"
[ "$#" -ge 2 ] && [ "$#" -le 3 ] || { echo "$usage" >&2; exit 2; }
kind="$1" path="$2" mode="${3:-}"
case "$kind" in bug|security) : ;; *) echo "$usage" >&2; exit 2 ;; esac
case "$mode" in ''|full) : ;; *) echo "$usage" >&2; exit 2 ;; esac
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
BUDGET=240000

# The shape git-worktree-detach.sh makes and git-worktree-drop.sh requires, so
# the one directory this writes is the sweep's own and never a caller's choice.
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
out="$resolved.slices"
[ ! -e "$out" ] || { echo "slices already cut: $out" >&2; exit 3; }

g() { git -C "$resolved" "$@"; }
pinned=$(g rev-parse --verify HEAD)
since=""
if [ "$mode" != full ] && last=$(g rev-parse --verify --quiet "refs/sweeps/$kind^{commit}"); then
  # A ref that is not an ancestor marks a sweep of another line of history, so
  # the diff from it would skip files this commit never had swept.
  if g merge-base --is-ancestor "$last" "$pinned"; then since="$last"; fi
fi

# One arm to a line: test_sweep_slices.py reads the rows each kind can return
# from these and holds each command's table to them. The order is the rule —
# the first arm that matches owns the path — so a spec is the suites' before it
# is the SPA's, and the SPA's own configuration is `config` by what is left.
# The row comes back in REPLY rather than on stdout: the loop below asks once
# per tracked file, and `$(row_of …)` forked a subshell for every one, about
# a tenth of a second each under MSYS, 52 s over this tree (#321).
row_of() {
  case "$kind:$1" in
    bug:src/Admin.Host/*.csproj|bug:src/Admin.Mcp/*.csproj) REPLY=config ;;
    bug:src/Admin.Host/*|bug:src/Admin.Mcp/*) REPLY=host ;;
    bug:src/Admin.Web/*.spec.ts|bug:src/Admin.Web/e2e/*|bug:tests/*) REPLY=suites ;;
    bug:src/Admin.Web/src/*) REPLY=spa ;;
    bug:src/Admin.Web/*) REPLY=config ;;
    security:src/*|security:tests/*) REPLY=source ;;
    *:tools/*|*:.github/*|*:.claude/*) REPLY=tooling ;;
    *:docs/*) REPLY=samples ;;
    */*) return 1 ;;
    *) REPLY=config ;;
  esac
}

# The bytes inside fences, the only part of a sample the sweeps audit, one line
# per blob of a `git cat-file --batch` stream: a header, the blob, and the
# newline cat-file adds after it. A fence closes only on its own character, at
# least as long, with nothing after it. A blob ends at a byte count, so
# LC_ALL=C: gawk's length counts characters in a UTF-8 locale, and one é
# miscounted would run a sample into the next. A stream that does not parse —
# a `missing` header, a blob cut short — fails rather than answering for it.
fenced_bytes() {
  LC_ALL=C awk '
    state == 2 { if ($0 != "") bad = 1; state = 0; next }
    state == 0 {
      if (NF != 3 || $2 != "blob") { bad = 1; exit }
      left = $3; open = 0; total = 0; state = 1; next
    }
    {
      left -= length($0) + 1; fence = 0
      if (match($0, /^[ \t]*(```+|~~~+)/)) {
        mark = substr($0, RSTART, RLENGTH); sub(/^[ \t]*/, "", mark)
        ch = substr(mark, 1, 1); n = length(mark); rest = substr($0, RSTART + RLENGTH)
        if (!open) { open = 1; och = ch; on = n; fence = 1 }
        else if (ch == och && n >= on && rest ~ /^[ \t]*$/) { open = 0; fence = 1 }
      }
      if (open && !fence) total += length($0) + 1
      if (left <= 0) { print total + 0; state = left == 0 ? 2 : 0 }
    }
    END { if (bad || state) { print "unreadable cat-file stream" > "/dev/stderr"; exit 1 } }'
}

# The lockfile's two promises, read mechanically because 340 KB is not a thing
# an auditor reads line by line: every resolved URL is on the npm registry over
# https, and every package entry that is not a link, bundled or the root carries
# a sha512 integrity; a link's resolved is a path, so a link is exempt from
# both. One fault a line. It is read as JSON, never as text: `npm ci` parses the
# file and ignores its whitespace, so a check that matched npm's own indentation
# was passed by an entry reformatted, or written on one line. A file that is not
# JSON, or has no package entries, fails closed, and so does a host with no
# Python 3 to read it.
lockfile_faults() {
  local python
  if command -v py >/dev/null 2>&1 && py -3 -c '' >/dev/null 2>&1; then python="py -3"
  elif command -v python3 >/dev/null 2>&1 && python3 -c '' >/dev/null 2>&1; then python=python3
  elif command -v python >/dev/null 2>&1 && python -c 'import sys; sys.exit(sys.version_info[0] != 3)' >/dev/null 2>&1; then python=python
  else echo "no Python 3 to read the lockfile"; return 0
  fi
  $python -I -c '
import json, sys
try:
    data = json.load(sys.stdin.buffer)
except Exception:
    print("the lockfile is not valid JSON")
    sys.exit(0)
version = data.get("lockfileVersion") if isinstance(data, dict) else None
if type(version) is not int or version not in (2, 3):
    print("lockfileVersion is not 2 or 3, so packages is not what npm ci reads")
    sys.exit(0)
packages = data.get("packages")
if not isinstance(packages, dict) or not packages:
    print("no package entries read")
    sys.exit(0)
for name, entry in packages.items():
    shown = ascii(name)[1:-1]
    if not isinstance(entry, dict):
        print("malformed entry: " + shown)
        continue
    if name == "" or entry.get("link") is True:
        continue
    resolved = entry.get("resolved")
    if "resolved" in entry and not (isinstance(resolved, str) and resolved.startswith("https://registry.npmjs.org/")):
        print("resolved off the registry: " + shown)
    integrity = entry.get("integrity")
    if not (isinstance(integrity, str) and integrity.startswith("sha512-")) and entry.get("inBundle") is not True:
        print("no integrity: " + shown)
'
}

# Each git read lands in a file with its status checked, NUL-separated, so a
# failing git fails the run and a name holding a newline stays one name. No
# arrays and no `mapfile`: macOS ships bash 3.2, and CI runs the suite there.
mkdir "$out"
# Any exit before the plan is printed takes the half-cut lists with it.
trap 'rm -rf "$out"' EXIT
g ls-tree -r -l -z --full-tree "$pinned" > "$out/.tree"
g grep -I -l -z -e '' "$pinned" -- . > "$out/.text" || [ "$?" -eq 1 ]
: > "$out/.changed"
[ -z "$since" ] || g diff --name-only --no-renames -z "$since" "$pinned" > "$out/.changed"
[ -s "$out/.tree" ] || { echo "no tracked files at $pinned" >&2; exit 3; }

# The text files and the changed ones as newline-bounded lists, tested with
# `case`: a name holding a line break is refused below before it can matter.
texts=$'\n' changes=$'\n'
while IFS= read -r -d '' f; do texts+="${f#"$pinned:"}"$'\n'; done < "$out/.text"
while IFS= read -r -d '' f; do changes+="$f"$'\n'; done < "$out/.changed"

tracked=0 unowned=0 unchanged=0 empty=0 binary=0 record=0 generated=0 fenceless=0 listed=0
sampled=0 lockfile=""
while IFS= read -r -d '' rec; do
  meta=${rec%%$'\t'*} file=${rec#*$'\t'}
  # A slice list is one name a line, so a name holding a line break cannot be
  # listed; refusing it is the only answer that does not drop it unsaid.
  case "$file" in *$'\n'*|*$'\r'*) echo "a tracked name with a line break" >&2; exit 3 ;; esac
  tracked=$((tracked + 1))
  size=${meta##* }
  row_of "$file" || { echo "no row owns: $file" >&2; unowned=$((unowned + 1)); continue; }
  row=$REPLY
  if [ -n "$since" ]; then
    case "$changes" in
      *$'\n'"$file"$'\n'*) : ;;
      *) unchanged=$((unchanged + 1)); continue ;;
    esac
  fi
  [ "$size" != 0 ] || { empty=$((empty + 1)); continue; }
  case "$texts" in
    *$'\n'"$file"$'\n'*) : ;;
    *)
      # Binary is skipped by an allowlist of what has nothing to audit, so a
      # code or configuration file git calls binary (a stray NUL byte, a binary
      # attribute) refuses the run by name and is never dropped as a count.
      # Matched without regard to case, by `nocasematch` (bash 3.1) rather
      # than a `tr` pipe, which forked twice per binary file.
      shopt -s nocasematch
      case "$file" in
        *.png|*.jpg|*.jpeg|*.gif|*.ico|*.webp|*.avif|*.bmp|*.woff|*.woff2|*.ttf|*.otf|*.eot|*.pdf|*.zip|*.gz|*.tgz|*.7z|*.jar|*.dll|*.exe|*.so|*.dylib|*.mp3|*.mp4|*.wav|*.webm)
          shopt -u nocasematch; binary=$((binary + 1)); continue ;;
        *) echo "git calls a file binary that is not a known asset: $file" >&2; exit 3 ;;
      esac ;;
  esac
  # The delivery plans are frozen records (CLAUDE.md), never edited to match the
  # code, so a defect in their samples has no fix to file; and the lockfile is
  # generated, too long for an auditor, so lockfile_faults reads it instead and a
  # fault refuses the run. Owned by their rows, counted, not listed.
  case "$file" in
    docs/superpowers/plans/*) record=$((record + 1)); continue ;;
    src/Admin.Web/package-lock.json) lockfile=$file; generated=$((generated + 1)); continue ;;
  esac
  # Both blob reads wait for the loop to end. Under MSYS each pipeline started
  # here was an emulated fork per file, most of a run's time (#577), so a
  # sample's name is written down and every sample is read in one pass below.
  if [ "$row" = samples ]; then
    printf '%s:%s\n' "$pinned" "$file" >> "$out/.samples"
    sampled=$((sampled + 1)); continue
  fi
  listed=$((listed + 1))
  printf '%s %s\n' "$size" "$file" >> "$out/.row.$row"
done < "$out/.tree"
rm -f "$out/.tree" "$out/.text" "$out/.changed"
[ "$unowned" -eq 0 ] || { echo "$unowned tracked path(s) no row owns" >&2; exit 3; }

if [ -n "$lockfile" ]; then
  faults=$(g cat-file blob "$pinned:$lockfile" | lockfile_faults)
  if [ -n "$faults" ]; then
    printf '%s\n' "$faults" | head -n 20 >&2
    echo "the lockfile fails its check: $lockfile" >&2; exit 3
  fi
fi

# Every sample through one cat-file and one awk, in tree order, the answers read
# back beside the names. Counted both ways, so a stream answering for fewer
# samples or more fails rather than pairing a count with the wrong name.
if [ "$sampled" -gt 0 ]; then
  g cat-file --batch < "$out/.samples" | fenced_bytes > "$out/.fenced"
  answered=0
  while IFS= read -r size <&4; do
    IFS= read -r sample <&3 || { echo "more fence counts than samples" >&2; exit 3; }
    answered=$((answered + 1))
    sample=${sample#"$pinned:"}
    case "$size" in ''|*[!0-9]*) echo "an unreadable fence count for: $sample" >&2; exit 3 ;; esac
    [ "$size" -gt 0 ] || { fenceless=$((fenceless + 1)); continue; }
    listed=$((listed + 1))
    printf '%s %s\n' "$size" "$sample" >> "$out/.row.samples"
  done 3< "$out/.samples" 4< "$out/.fenced"
  [ "$answered" -eq "$sampled" ] ||
    { echo "fences read for $answered of $sampled samples" >&2; exit 3; }
  rm -f "$out/.samples" "$out/.fenced"
fi
if [ -z "$since" ] && [ "$listed" -eq 0 ]; then
  echo "a full run that lists nothing has read nothing" >&2; exit 3
fi

echo "pinned $pinned"
if [ -n "$since" ]; then echo "mode since $since"; else echo "mode full"; fi
echo "tracked $tracked listed $listed unchanged $unchanged empty $empty binary $binary record $record generated $generated fenceless $fenceless"
n=0
for rowfile in "$out"/.row.*; do
  [ -e "$rowfile" ] || continue
  row=${rowfile##*/.row.}
  bytes=0 files=0 list=""
  flush() {
    [ "$files" -gt 0 ] || return 0
    n=$((n + 1))
    printf '%s' "$list" > "$out/$n.txt"
    echo "slice $n $row $files $bytes $out/$n.txt"
    bytes=0 files=0 list=""
  }
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    size=${line%% *} file=${line#* }
    [ $((bytes + size)) -le "$BUDGET" ] || flush
    bytes=$((bytes + size)) files=$((files + 1)) list+="$file"$'\n'
  done < "$rowfile"
  flush
done
rm -f "$out"/.row.*
trap - EXIT
