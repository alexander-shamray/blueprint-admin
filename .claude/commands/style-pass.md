---
description: Apply one code-style form corpus-wide, then record it in docs/style-guide.md and the linters
argument-hint: "<the corrected form — paste the code as it should read>"
allowed-tools: Read, Grep, Glob, Edit, Bash(git diff:*), Bash(wc:*), Bash(ls:*), Bash(bash .claude/scripts/npm-checks.sh:*), Bash(bash .claude/scripts/host-checks.sh:*)
disallowed-tools: Bash(git push:*), Edit(.claude/**), Edit(./.claude/**), Edit(.github/**), Edit(./.github/**), Edit(.remember/**), Edit(./.remember/**), Edit(android/**), Edit(./android/**), Edit(ios/**), Edit(./ios/**), Edit(.git/**), Edit(./.git/**), Edit(package.json), Edit(./package.json), Edit(package-lock.json), Edit(./package-lock.json), Edit(npm-shrinkwrap.json), Edit(./npm-shrinkwrap.json), Edit(.npmrc), Edit(./.npmrc), Edit(angular.json), Edit(./angular.json), Edit(tsconfig.json), Edit(./tsconfig.json), Edit(tsconfig.app.json), Edit(./tsconfig.app.json), Edit(tsconfig.spec.json), Edit(./tsconfig.spec.json), Edit(capacitor.config.ts), Edit(./capacitor.config.ts), Edit(playwright.config.ts), Edit(./playwright.config.ts), Edit(ionic.config.json), Edit(./ionic.config.json), Edit(.nvmrc), Edit(./.nvmrc), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(README.md), Edit(./README.md), Edit(**/package.json), Edit(**/.npmrc), Edit(**/tsconfig*.json), Edit(**/angular.json), Edit(**/package-lock.json), Edit(node_modules/**), Edit(./node_modules/**), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(global.json), Edit(./global.json), Edit(BlueprintAdmin.slnx), Edit(./BlueprintAdmin.slnx), Edit(**/*.csproj), Edit(AGENTS.md), Edit(./AGENTS.md), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore)
---

A corrected code form has been given:

$ARGUMENTS

It is an instance of a rule, not a one-off fix. Four things follow, and a pass
that does fewer than four is not done.

## 1. Name the rule

State it in one sentence before touching anything, and say what it implies for
cases the example does not show. `private readonly http = inject(HttpClient);`
replacing a constructor parameter is not "use inject here" — it is *a class
takes its dependencies through `inject()` at field level, never through a
constructor parameter*, which then decides what happens to a class that also
needs a constructor for something else, and to the `super()` call in one that
extends another.

If the rule as stated would contradict something `docs/style-guide.md` or
`CLAUDE.md` already says, stop and say which. That is a decision about the
whole corpus and it is the user's.

## 2. Sweep the corpus

The named site is one of many. Find the rest across `src/Admin.Host/**`,
`src/Admin.Web/**`, `tests/**` and `src/Admin.Web/e2e/**`, and across every
fenced block in `docs/` — the spec and the plan carry samples, and one
dialect governs all of it. A rule applied to the source and not to the
samples leaves the samples teaching the form the pass just removed.

Grep gets you candidates; reading them gets you the shape. Anything structural
— bracket depth, chain heads, continuation columns, template boundaries — has
to track whether it is inside a TypeScript fence, an HTML fence, an inline
`template:` string inside a component, or prose, because the rules differ per
context, and that tracking is done by reading the file rather than by a script
this command can run unattended.

**This command holds no interpreter grant, and the absence is deliberate.** It
used to carry `Bash(python:*)`, which is a prefix grant on a general-purpose
interpreter: `python -c "<anything>"` was auto-approved, and one `open(…,'w')`
reaches every path the `Edit(…)` denies cover — `.claude/scripts/**`,
`.claude/sandbox/**`, `.claude/commands/**`, `.claude/agents/**`,
`.claude/settings.json`, `.claude/settings.local.json` and `.remember/**` —
while one `subprocess.run` reaches every `git push --force` and `git switch -fC`
the deny list and the `git-*.sh` helpers exist to keep out. A rule that
matches `python -c` cannot see what the interpreter then does, so the whole
control structure this repository documents sat downstream of one line of
frontmatter. That matters here more than in most commands because the input is
attacker-influenced twice over: `$ARGUMENTS` is a pasted code form, and the
sweep reads `docs/**` and `src/**`, which a PR author controls. **`Bash(npx:*)`
is the same grant wearing a different name** and is refused for the same
reason: `npx` fetches and executes a package nobody in this repository has
pinned, so it is an interpreter grant with a download in front of it.

A throwaway script is still the right tool for a genuinely structural sweep.
Write it into the scratchpad and run it — under the default permission mode the
run prompts, and one approval per pass is the whole cost.

**The prompt is not the boundary, and this paragraph should not be read as
claiming it is.** Dropping the grant removes *command-level auto-approval*;
what an unlisted tool then does is the active permission mode's decision, and
under a bypassing mode it runs silently — the same premise `CLAUDE.md` states
for the sweeps' denies. So what this change buys is that the interpreter is no
longer pre-approved by this command, not that a human sees every run. A command
that needed the stronger property would have to refuse those modes, and none
here does.

**Confirm each hit by reading it.** The recurring false positives are real and
they repeat: a `)` closing an RxJS `pipe(` mid-chain is a continuation, not a
chain head; an interpolation inside a template literal is not string
concatenation; an arrow function passed to `map` is not a lambda you may
reformat into a block without changing what it returns; a `#private` field is
not a TypeScript `private` modifier and the two do not accept the same edits;
and a `.spec.ts` file is source, not a sample.

## 3. Record it in `docs/style-guide.md`

The rule goes in that file's TypeScript, template or prose section, in the
established voice: the rule, an example, and **the reason it is not
arbitrary**. State the exceptions with them — half that file's value is the
carve-outs, and a rule recorded without its exceptions gets applied to the
exceptions next time.

**`CLAUDE.md` keeps a short list of these rules under *Style*, and the guide
is the master copy.** If the form you corrected is one of the ones it names,
the change has to reach both in this pass — a rule in two places that moves
in one of them is the drift the split was accepted in order to buy.

**Do not write a site count into the guide, and remove one you find.**
`docs/change-locality.md` §2 forbids a document stating a number of sites in
the present tense, and this step used to instruct the opposite — keep the
counts current — which is how the guide came to carry `23 sites do this`
until a review round removed it. State the convention and let the corpus be
its own inventory. A count belongs in the commit body or the PR body, where
§2 permits it as a measurement taken at a named commit.

## 4. Reconcile the linters

**Every time. Without being asked.** There are three files and they do not
overlap, so the question is not "which one" but "which of the three, and why
not the other two".

| | |
|---|---|
| `src/Admin.Web/.prettierrc` | Layout Prettier owns outright: print width, quotes, semicolons, trailing commas, and every wrapping decision in `.ts` and `.html` |
| `src/Admin.Web/eslint.config.js` | Anything expressible as a rule about the *program* rather than its layout — selector prefixes, the template accessibility set |
| `.editorconfig` | The few keys an editor applies before either runs: indent, charset, final newline, trailing whitespace, and `quote_type` for `.ts` |

Four outcomes, and the last is as important as the others:

- **Prettier already decides it** — then the rule is not yours to state, and
  the pass is a reformat plus a guide entry saying Prettier owns it. Do not
  add an ESLint rule that re-litigates a Prettier decision: the two disagree
  eventually, and the disagreement surfaces as a lint failure on a file
  Prettier just wrote.
- **A Prettier option expresses it** — set it in `src/Admin.Web/.prettierrc`, and record what
  it costs in the guide rather than in the JSON, which takes no comments. Then
  reformat the corpus in the same pass: changing an option and not running it
  leaves every unvisited file wrong and the next unrelated edit carrying the
  reformat.
- **An ESLint rule expresses it** — add it to `src/Admin.Web/eslint.config.js` in the
  narrowest `files:` block that covers the rule, with a comment saying what it
  cannot catch. The existing `no-restricted-imports` blocks are the model:
  each says in its own comment where it over-fires and where it under-fires,
  and names `boundaries.spec.ts` as the precise check. **A rule that hides its
  blind spot is worse than no rule**, because the next reader takes the rule's
  existence as coverage.
- **Nothing expresses it** — say so in the guide's "no setting expresses
  these" list, so a reviewer does not go hunting for a key that does not
  exist. Never invent an option name: Prettier fails closed on an unknown key
  and ESLint fails closed on an unknown rule, so an invented one breaks
  `npm run lint` for everybody rather than being ignored.

**Prettier is not in `npm run lint`, and that is a gap to state rather than to
assume.** `ng lint` runs ESLint only, `package.json` carries no `format`
script, and no CI job checks formatting — so a Prettier-owned rule is enforced
by editors and by review and by nothing else. The guide should say which rules
are in that position. Wiring `npx prettier --check .` into the `web` job is a
follow-up worth having, and it is a decision about the build rather than about
this pass.

## 5. Verify

Re-scan for the invariants the corpus holds. Every one of these has caught a
real regression introduced by a previous pass:

- no line over 100 columns inside a code fence — `src/Admin.Web/.prettierrc`'s `printWidth`
- single quotes at every `.ts` site, source and sample alike
- every continuation indent a multiple of two
- no ragged list — one line, or one element per line
- fences balanced, **LF** intact — `.gitattributes` is `* text=auto eol=lf`,
  and a CRLF that reaches the tree breaks the `.claude/scripts` shebangs — and
  no trailing whitespace

Then run the linters themselves — `bash .claude/scripts/npm-checks.sh lint`,
which this command is granted — and report what it said rather than what you
expect it to say. A pass that touched `src/Admin.Web/.prettierrc` also wants
`npx prettier --check .`, which this command is **not** granted, for the
reason §2 gives: run it and take the prompt, one approval, the same cost as
the throwaway script.

## Report

The rule as stated, sites changed per file, the `docs/style-guide.md`,
`CLAUDE.md`, `src/Admin.Web/.prettierrc`, `src/Admin.Web/eslint.config.js` and `.editorconfig` edits, and
the invariant scan results as numbers. Flag every judgement call where two of
the rules both applied and one had to win — do not bury it.
