# The locality gate

**The claim: a pull request's diff sits inside the change class its body
declares and inside the touch set it declares, leaves no path in the tree
that the tree's `.gitignore` excludes, and touches no path the owner holds.**
[`docs/change-locality.md`](../../docs/change-locality.md) §3 is the rule
for the first two; this gate is §3 enforced.

## The two rows

`docs/change-locality.md` asks every PR body to carry two rows, `| Class |`
and `| Touch set |`, and section 3 gives each class a tree set.

Every changed path is judged twice, and a path outside either set fails the
PR and is named in the verdict. The class -> tree-set map in `classes.yml`
beside this file says what a class may reach in this repository, but it cannot
say "one Host area" or "one Web feature": a Host change that also edits the SPA
is inside Class A's map, which is why the declared touch set is the second
check. A `+`-joined class is the union of its members' maps.

The body carries exactly one row of each, or the run is refused with exit 2
rather than judged. Half the metadata makes half the gate impossible, so a
missing row, a repeated one, a class letter outside A-E, a repeated member or
prose where a path list should be is a refusal naming the row, never its
content.

## The map

The map is read by a parser that accepts the one shape its header states,
because there is no stdlib YAML parser and a gate that needs a `pip install`
gets skipped. A line outside that shape, a missing class, a repeated class or
a class with no items refuses the whole map: a half-read map is a gate reading
a file other than the one a reader sees.

The glob dialect and the row grammar are `pr-locality.sh`'s. `**` crosses
directories, `*` and `?` do not, `{a,b}` is an alternation, a token also
covers everything beneath the directory it names, and every token is
repository-relative: no leading `/`, no `./`, no `..` segment, brace
alternatives included. The harness helper cannot run Python under its grant,
and CI fetches its own payload, `changedFiles` and the files endpoint included,
rather than the helper's field set, so the grammar has two implementations,
which must accept and refuse the same tokens.

## The changed paths

The touch-set cell is never printed, because a PR author is not a trusted
party. A changed path is the author's text too, since git permits a newline
inside a name, so each must be a plain path and one that is not refuses the
run: a verdict list with a line withheld reads as complete.

The file list can be short in two ways that its own paths do not show. The
files endpoint returns a bounded number of entries however it is paginated, so
the payload carries GitHub's `changedFiles` count and a shorter list is
refused. A rename arrives as one entry with the source in `previous_filename`,
so both ends of a rename are judged.

## Ignored paths and the owner's paths

Some paths are judged on more than their class. A path the diff leaves in the
tree that this checkout's ignore rules match is refused (#799): a force-added
`.claude/settings.local.json` is tracked, which git's own `check-ignore` passes
over unless it is told not to read the index, and once on `main` it reaches
every clone and every fork with grants and hooks no settings render controls.
The ask is `check-ignore --no-index`, case-folded as a Windows checkout reads
the rules, with no user excludes file, and a git that cannot answer refuses the
run rather than reading as "none ignored". A `removed` entry leaves nothing in
the tree, so it is not judged.

The ignore rules are the checkout's, so a pull request that also un-ignores the
path passes that check. So the paths `OWNER_ONLY` in `locality_gate.py` names,
`.claude/settings.local.json` and `.mcp.json` (#797), are refused in every
class whatever the ignore rules say, each for the reason it carries there; the gate's code is
taken from the base commit, so the same pull request cannot lift that.
`docs/harness-local.md` *Residuals* states what this leaves open.

## Running it

Stdlib only, for the reason *The map* gives. The deciding takes JSON on stdin
and the fetching is two `gh` calls in the workflow. A file entry is a plain path or the endpoint's own
`{filename, previous_filename, status}`. `--root` names the checkout whose
ignore rules judge, the working directory by default.

```json
{"number": 190, "body": "<the PR body>", "changedFiles": 2,
 "files": [{"filename": "<path>", "previous_filename": null,
            "status": "added"}, ...]}
```

```bash
gh api "repos/{owner}/{repo}/pulls/<n>/files" --paginate \
    --jq '.[] | {filename, previous_filename, status}' |
  jq -s --argjson pr "$(gh pr view <n> --json number,body,changedFiles)" \
    '$pr + {files: .}' |
  py -3.12 .github/locality-gate/locality_gate.py
```

Exit 0 passes, 1 names each problem, 2 refuses an input it cannot judge.
`python -m unittest` in this directory is its suite.
