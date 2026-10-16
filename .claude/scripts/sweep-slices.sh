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

# One arm to a line: test_sweep_slices.py reads the rows each kind can print
# from these and holds each command's table to them. The order is the rule —
# the first arm that matches owns the path — so a spec is the suites' before it
# is the SPA's, and the SPA's own configuration is `config` by what is left.
row_of() {
  case "$kind:$1" in
    bug:src/Admin.Host/*.csproj) echo config ;;
    bug:src/Admin.Host/*) echo host ;;
    bug:src/Admin.Web/*.spec.ts|bug:src/Admin.Web/e2e/*|bug:tests/*) echo suites ;;
    bug:src/Admin.Web/src/*) echo spa ;;
    bug:src/Admin.Web/*) echo config ;;
    security:src/*|security:tests/*) echo source ;;
    *:tools/*|*:.github/*|*:.claude/*) echo tooling ;;
    *:docs/*) echo samples ;;
    */*) return 1 ;;
    *) echo config ;;
  esac
}

# The bytes inside fences, the only part of a sample the sweeps audit. A fence
# closes only on its own character, at least as long, with nothing after it.
fenced_bytes() {
  awk '
    match($0, /^[ \t]*(```+|~~~+)/) {
      mark = substr($0, RSTART, RLENGTH); sub(/^[ \t]*/, "", mark)
      ch = substr(mark, 1, 1); n = length(mark); rest = substr($0, RSTART + RLENGTH)
      if (!open) { open = 1; och = ch; on = n; next }
      if (ch == och && n >= on && rest ~ /^[ \t]*$/) { open = 0; next }
    }
    open { total += length($0) + 1 }
    END { print total + 0 }'
}

# The lockfile's two promises, read mechanically because 340 KB is not a thing
# an auditor reads line by line: every resolved URL is on the npm registry over
# https, and every package entry that is not a link, bundled or the root carries
# a sha512 integrity. One fault a line; a file with no entry indented the way npm
# writes them has nothing read, so that fails closed too.
lockfile_faults() {
  awk '
    /^    "[^"]*": \{[ \t]*$/ {
      name = $0; sub(/^    "/, "", name); sub(/": \{.*$/, "", name)
      has = 0; link = 0; bundled = 0; entries++; next }
    /^      "resolved": / {
      if ($0 !~ /^      "resolved": "https:\/\/registry\.npmjs\.org\//)
        print "resolved off the registry: " name }
    /^      "integrity": "sha512-/ { has = 1 }
    /^      "link": true/ { link = 1 }
    /^      "inBundle": true/ { bundled = 1 }
    /^    \},?[ \t]*$/ {
      if (name != "" && !has && !link && !bundled) print "no integrity: " name
      name = "" }
    END { if (entries == 0) print "no package entries read" }'
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
while IFS= read -r -d '' rec; do
  meta=${rec%%$'\t'*} file=${rec#*$'\t'}
  # A slice list is one name a line, so a name holding a line break cannot be
  # listed; refusing it is the only answer that does not drop it unsaid.
  case "$file" in *$'\n'*|*$'\r'*) echo "a tracked name with a line break" >&2; exit 3 ;; esac
  tracked=$((tracked + 1))
  size=${meta##* }
  row=$(row_of "$file") || { echo "no row owns: $file" >&2; unowned=$((unowned + 1)); continue; }
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
      case "$(printf '%s' "$file" | tr 'A-Z' 'a-z')" in
        *.png|*.jpg|*.jpeg|*.gif|*.ico|*.webp|*.avif|*.bmp|*.woff|*.woff2|*.ttf|*.otf|*.eot|*.pdf|*.zip|*.gz|*.tgz|*.7z|*.jar|*.dll|*.exe|*.so|*.dylib|*.mp3|*.mp4|*.wav|*.webm)
          binary=$((binary + 1)); continue ;;
        *) echo "git calls a file binary that is not a known asset: $file" >&2; exit 3 ;;
      esac ;;
  esac
  # The delivery plans are frozen records (CLAUDE.md), never edited to match the
  # code, so a defect in their samples has no fix to file; and the lockfile is
  # generated, too long for an auditor, so lockfile_faults reads it instead and a
  # fault refuses the run. Owned by their rows, counted, not listed.
  case "$file" in
    docs/superpowers/plans/*) record=$((record + 1)); continue ;;
    src/Admin.Web/package-lock.json)
      faults=$(g cat-file blob "$pinned:$file" | lockfile_faults)
      if [ -n "$faults" ]; then
        printf '%s\n' "$faults" | head -n 20 >&2
        echo "the lockfile fails its check: $file" >&2; exit 3
      fi
      generated=$((generated + 1)); continue ;;
  esac
  if [ "$row" = samples ]; then
    size=$(g cat-file blob "$pinned:$file" | fenced_bytes)
    [ "$size" -gt 0 ] || { fenceless=$((fenceless + 1)); continue; }
  fi
  listed=$((listed + 1))
  printf '%s %s\n' "$size" "$file" >> "$out/.row.$row"
done < "$out/.tree"
rm -f "$out/.tree" "$out/.text" "$out/.changed"
[ "$unowned" -eq 0 ] || { echo "$unowned tracked path(s) no row owns" >&2; exit 3; }
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
