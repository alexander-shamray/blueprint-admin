# Harness boundaries

**What the agent harness grants this repository's commands, what it refuses
them, and every place a grant is wider than the operation it was added for.**
Load-bearing whenever you touch `.claude/` — a command's frontmatter, a
helper, `settings.json`, a hook — and inert for every other kind of change.

**This harness was ported from `alexander-shamray/blueprint-frontend`, which
ported it from `alexander-shamray/blueprint-backend`.** A bare `#NN` under
`.claude/` or in a copied helper comment is an issue in **this** repository,
and reads that way. A citation to a sibling names it (`blueprint-frontend#20`,
`blueprint-backend#1086`). A number a port left bare is a defect to qualify,
not a second meaning: the redirection-target comment in
`.claude/hooks/guard-git-argv.py` (near line 192) cites the sibling's
writing-verb issue as `blueprint-frontend#26`, which a bare `#26` would
resolve to the wrong repository. Where the context decided the sibling, the
citation is qualified; the few it did not (the `#55, #56, #68` group, and the
closure-keyword examples in `pr.md` and `commit.md`) stay bare and are owed
under #448. A bare number not named here is a local issue. The arguments
for the grants, the deny lists, the git-argv hook and the edit-target hook live
in those two
`docs/harness-boundaries.md` files and are not restated here; a summary of an
argument is how a rule gets "corrected" back.

**Index — grep this file for the bold lead-in, never read it whole.** Each
line names one residual below by the words it opens with.

| Touching | Grep for |
|---|---|
| The check helpers, e2e, code the session writes | `Two toolchains`, `runs unjudged through a granted check helper` |
| `settings.json` or a permission rule | `File permission rules take`, `self-locks`, `The global deny list names` |
| Whether a guard finding is filed | `The guards catch honest mistakes` |
| A guard hook or its launcher | `Every guard hook uses`, `The git-argv hook answers` |
| The index: refresh, query or search hints, MCP approval, timeout | `Two hooks guard nothing`, `The second is the query hint`, `The search hint asks`, `A session started in a worktree`, `The CLI's skill rewrite`, `The MCP startup timeout` |
| A command's `allowed-tools` or `disallowed-tools` | `grant the index commands`, `runs before it pushes never denies push`, `is a grant surface`, `A command that holds` |
| The review agent grant, or restoring an external reviewer | `grants exactly`, `archive/external-reviewers` |
| The harness suite and its shards | `shard-harness-suite.py`, `That runner owns every discovery root`, `One run at a time, per user` |
| The sweeps' slice lists and `refs/sweeps/` | `The sweeps write two things` |
| Rebasing or force-pushing | `The only force push`, `A second helper rewrites` |
| `/branch` worktrees under `.claude/worktrees/` | `worktrees sit inside the checkout` |
| MSBuild/NuGet auto-imports, agent types, `/style-pass` and its linter | `The files MSBuild and NuGet`, `is denied beside the built-in agent types`, `runs the linter it configures` |

A new residual is stated **here** and gets a row above.

**The guards catch honest mistakes, not a session that sets out to defeat
them.** Which guard findings are filed is
`alexander-shamray/blueprint-backend`'s `docs/harness-boundaries.md`,
*The guards' threat model*, and it holds here unchanged: a spelling an agent types in
ordinary work, a guard failing open on good-faith input, or a false refusal is
a finding; a spelling built only to hide a command from the hook is recorded
as out of scope, never filed.

**What this repository adds, and is therefore the only residual stated here:**

- Two toolchains. `/ship` and `/review-branch` run
  `bash .claude/scripts/host-checks.sh` (`dotnet test BlueprintAdmin.slnx`)
  and `bash .claude/scripts/npm-checks.sh` (lint / test / build **inside**
  `src/Admin.Web`). There is no `e2e` mode: Playwright needs the Host under
  FakePlatform, and a suite that passed because nothing was listening would
  be worse than none. CI's `smoke` job is that check.
- **Code the session writes runs unjudged through a granted check helper, and
  only the trust boundary stands.** `/ship` holds `Write` and `Edit` over `src/**`,
  `tests/**` and `tools/**` beside `host-checks.sh`, `npm-checks.sh` and
  `harness-checks.sh`, which run what the tree holds: a written test, spec or
  `tools/token-usage/test_*.py` module executes as host code that no hook judges
  (#310). That is the job (a change to code is a change to its tests), so the
  residual is stated and not closed: the unattended chain's only input is the branch
  it works on, the review reads the diff before the merge, and `main`'s ruleset
  (`protect-main`, id 23805274, read with `gh api repos/.../rules/branches/main`,
  which lists `required_status_checks`) requires CI. **`/pr` is not narrowed to its two files, and the reason is
  structural**: a frontmatter deny holds for the rest of the user turn, and `/ship`
  runs `/pr` at step 4 and then applies review fixes to `src/**` and `tests/**` at
  step 5, so `Edit(src/**)` in `/pr` would refuse the chain's own fixes. `tools/**`
  stays editable in `/ship` because the token-usage tooling changes through it.
- **File permission rules take `Edit(...)`, never `Write(...)`** — a
  `Write(path)` rule matches nothing and stops Claude Code from starting.
- **`.claude/settings.json` self-locks, not instantaneously** — a change to
  it lands complete and goes last, and a restore is verified by reading the
  file, never by trying what it forbids.
- **Every guard hook uses `run-guard.sh`** — the two in `settings.json` — which locates a compatible Python
  launcher before invoking the guard. **It runs each candidate before
  choosing one, and remembers only that the first of them passed (#44).** The
  mark is an empty directory under `.claude/cache/`, named for that
  candidate, and the launcher never opens it: it holds nothing to choose a
  program with, nothing to write through, and nothing to block on — a file
  there could be a FIFO, or a newline-free file for `read` to sit in, and a
  hook that hangs is timed out, which does not block the tool. No
  `Edit(...)` deny covers the directory and none has to: the most a session
  can leave there is a skipped probe of the program the probe would have
  run, or a mark it cannot remove, which refuses every call until somebody
  does. **A remembered interpreter is never `exec`ed**, because an `exec`
  that fails exits non-zero and not 2, and a `PreToolUse` hook that exits
  any other way is a non-blocking error — the tool then runs unguarded. It
  runs `run-guard.py` as a child instead, which checks the floor in the
  interpreter that is about to judge and answers with a status nothing else
  is known to produce; on any other status the call is refused, the mark is
  removed and the next call probes. **The residual is that one refused
  call**, where the probe used to move on to the next candidate unnoticed,
  and a host whose first candidate is not its working one is never
  remembered at all. `TheLauncherProvesWhatItRemembers` and
  `ARememberedGuardAnswersAsAProbedOne` in `test_harness_helpers.py` hold the
  launcher to each of those.
- **Two hooks guard nothing, and both serve the index.** The first is the
  refresh, which runs
  `refresh-index.sh` from two events: a `PostToolUse` entry after every tool
  that writes, Bash included, and a `SessionStart` entry for the changes no
  tool makes. Bash is there because agents write through it most — a commit,
  a pull, a formatter, an edit script — and each left the index describing
  the tree it replaced while reporting itself fresh (#100); a call that wrote
  nothing costs one `update` that changes nothing, in the background. One
  command serves both events, because the script resolves its repository with
  `git rev-parse` from the edited file's directory when the event names a
  `file_path` or `notebook_path` — so a worktree's file edited from the main
  checkout refreshes the worktree's index (#101) — and otherwise from the
  directory the event names as `cwd`, which follows a worktree the session
  entered and a Bash `cd` where `${CLAUDE_PROJECT_DIR}` stays at the start
  folder, and from its working directory when no event names one. Reading
  that payload is why neither
  entry ends in `&` — an asynchronous list's stdin is `/dev/null` — so the
  script detaches its own refresh, and both entries silence it. It can never
  block a tool call, which also means a broken spelling fails on every call
  without a sound — the example it replaced passed a `--quiet` the CLI does
  not have. The script coalesces
  overlapping calls so an `update` always starts after the last edit, and
  retries a failed one. In a linked worktree with no index it first copies
  the main checkout's `index.sqlite` in, and only that file, so a `/branch`
  workspace is indexed in seconds rather than by a full build inside the
  agent's turn; a main checkout with no index leaves it with none.
  `git-worktree-fork.sh` starts it once in each new worktree, because
  `/branch` enters one mid-session, where no `SessionStart` fires.
  `test_index_refresh_hook.py` runs it against a fake
  CLI, and it pins the two events, the matcher and the
  `CBX_NO_SKILL_AUTO_UPDATE` guard `.mcp.json` sets, in the script and in the
  skill's `examples/hooks/settings.json` alike — the events by their whole
  set, so a third added to either file without a test of its own is a red case
  rather than a silent one. The example stays a self-contained one-liner,
  because the script is this repository's and not the skill's, and so it
  reads no event and refreshes the working directory's checkout. Both
  call the CLI bare, as `.mcp.json` does, rather than through the skill's
  `cbx` wrapper: a hook shell's `bash` can resolve to WSL's on Windows, which
  cannot run it.
- **The second is the query hint (#91)**: `index-query-hint.sh` on
  `UserPromptSubmit`, because keeping the index current and telling sessions
  to use it — `CLAUDE.md` and the skill's `description` both do — did not get
  it read. A prompt that reads as a locate, explain,
  references, impact or named-symbol question gets one line of
  `additionalContext`: load the skill, the subcommand its route table gives
  that question, and the session-tag rule; every other prompt gets nothing,
  and so does every slash command but `/ship` and `/branch`, whose arguments
  are read as the task they carry (#97). **It asks for the skill and not only
  the command** because outside the two commands below the skill's
  `allowed-tools` is the only grant for `codebase-index …`, and it holds only
  while the skill is loaded. **It runs
  under plain `sh` and not through `run-guard.sh`**, whose every failure is 2 —
  under this event 2 blocks the prompt and erases it, so the hint leaves with
  0 whatever happened. It matches only the decoded `prompt` string, never the
  payload's paths, writes nothing and spawns one `awk`.
  `test_index_query_hint.py` runs it on every pattern, on phrases that are
  not the question, on slash commands, unreadable input and an `awk` that
  fails, and holds every command it emits to a row of the skill's route
  table and a subcommand its `allowed-tools` approves. Hooks are read when a
  session starts: the hint reaches a session opened after it lands.
- **The search hint asks before the first symbol search, once (#119, #120).**
  `index-search-hint.py` runs under `PreToolUse` on `Grep` and `Bash` through
  `index-hint-run.sh`. The first form of this fix, a note attached to a grep's
  result, was shipped in `blueprint-backend` and the agent read it and grepped
  on: it arrived after the answer, so asking the index meant redoing a search
  that had succeeded. So the call itself is refused, once per session, with
  the lookup that answers it as the reason, and the same search rerun passes —
  the one soft block the agent cannot read past. **It refuses only a search
  for a name**: an identifier, or an alternation of identifiers, over the tree.
  Text, a message, a pattern, one file, prose under `docs/` and a path outside
  the checkout are grep's and are never touched; a session whose transcript
  records a lookup that ran is never asked, and a subagent never is. **One
  route, the one that answers from this tree:** the MCP server reads the folder
  the session started in, so from that checkout the reason names the MCP
  tools, and from another, a `/branch` worktree entered mid-session, it names
  the CLI alone (#119 item 4; the server does not follow the move). The
  launcher exits 0 on every path, because 2 blocks the tool call; past its
  bound or on a failed start it prints a line saying the hint did not run, so
  a silent hook is not read as a clean search. `index-query-hint.sh` names the
  MCP tool beside the CLI command from a map `test_index_search_hint.py` holds
  equal to the hook's. Hooks are read when a session starts.
- **A session started in a worktree has the index's MCP tools (#117).**
  Approval of `.mcp.json` servers lives in the main checkout's untracked
  `settings.local.json`, which a fresh worktree lacks, so its session reported
  `Pending approval` and searched with grep. `git-worktree-fork.sh` writes the
  worktree's own file holding `enabledMcpjsonServers` and nothing else: an
  approve-all crosses as the names `.mcp.json` defines at the fork, a
  `disabledMcpjsonServers` name is dropped, and the main checkout's
  `permissions` rows never cross. A shape it cannot copy warns and leaves the
  fork done. `test_git_worktree_fork.py` runs each case.
- **`/ship` and `/review-branch` grant the index commands they name (#97)**,
  in their own `allowed-tools`: `/ship` locates a change with `search`,
  `refs` and `impact`, and starts every review round from `diff-impact`
  against the merge base, read through an exact
  `Bash(git merge-base origin/main HEAD)`; a full `/review-branch` starts from
  `diff-impact` too. All four are read-only. **Loading the skill widens that,
  and it is a residual rather than an oversight:** the skill's grant also
  approves `update` and `index`, which its evidence protocol runs on a stale
  or missing index, and both write only the ignored cache. A hint cannot carry this
  for `/ship`, whose arguments are usually an issue number, so the command
  says it itself.
  `test_index_query_hint.py` holds every subcommand either names to its own
  grant and to the skill's.
- **The CLI's skill rewrite is off for every session, in
  `.claude/settings.json`'s `env` (#97).** `codebase-index` re-materialises
  its packaged skill over the tracked one whenever the skill's ignored
  `.skill_version` stamp differs from the installed package. A `/branch`
  worktree has no stamp, so the first bare call there replaced the tracked
  `SKILL.md`, its `allowed-tools` widened to `Bash(cbx *)` and `graph` among
  them — measured on #97's own worktree. `.mcp.json` and `refresh-index.sh`
  set `CBX_NO_SKILL_AUTO_UPDATE=1` for their own calls; an agent's Bash call,
  which the hint and `/ship` now ask for, is covered by `env` and nothing
  else. `test_index_query_hint.py` pins it.
- **The MCP startup timeout is raised in the same `env`, as `MCP_TIMEOUT`
  (#107).** The plugin servers — `playwright` and `chrome-devtools`, both
  started through `npx` — failed their first start after an idle machine:
  34 of 323 logged starts on this workstation from July to 2026-10-04 hit
  the 30 s default, a third of all starts that came more than two hours
  after the last one against under 4% of the rest, and only two of the 34
  had a second session starting in the same minute. Concurrency was the
  issue's guess and is not the cause. The successful starts reach 29.7 s,
  so the tail runs into the old limit rather than stopping short of it; a
  warm start measured 2–3 s, a cold `npx` cache 8 s, and three sessions at
  once at most 10 s, every server connected. **The cost of a timeout is
  not one session**: Claude Code caches a plugin stdio server's failure
  for 15 minutes in the user-level `~/.claude/mcp-needs-auth-cache.json`,
  keyed by server name, so every session on the machine starts without
  it, in any repository. `.mcp.json`'s `codebase-index` is not a plugin
  and is never cached, which is why it never dropped out. The value is
  set high because a long limit only delays the report on a server that
  is truly broken, and the session does not wait on it; it covers only
  sessions started here, so a cold start in another repository can still
  set the cache — the same key in the user's own `settings.json` `env`
  closes that. The failures were all connection timeouts, never tool
  calls, so `MCP_TOOL_TIMEOUT` is left at its default.
- **`.claude/skills/**` is a grant surface.** A skill's `allowed-tools` is
  auto-approval, so a session that can rewrite `SKILL.md` widens the next
  invocation. Commands, agents, hooks and settings were already denied;
  both `Edit(.claude/skills/**)` spellings sit on the same list.
- **A command that holds `Write` or `Edit` denies the user's memory and
  output styles (#280).** `~/.claude/projects/*/memory/` is loaded into later
  sessions as the user's own standing instruction, and `~/.claude/output-styles/`
  shapes how they answer; a command that reads a branch diff and holds `Write`
  could otherwise plant one. `guard-edit-target.py` admits the rest of
  `~/.claude` on purpose, since a session's own memory and scratch writes live
  there, and it cannot tell a save from an injection, so the two `Edit(~/.claude/...)`
  denies sit in `disallowed-tools` of `/ship`, `/pr`, `/style-pass` and
  `/review-branch`, and `AWritingCommandDeniesTheUsersMemoryAndOutputStyles`
  fails when a command that grants either tool lacks them. A session inside one
  of those commands therefore cannot save a memory: it saves it afterwards.
  **Measured, not only present (#291).** On 2026-10-20, on Windows, in a turn
  whose `/ship` frontmatter was in force: a `Write` to
  `~/.claude/projects/<repo>/memory/` and one to `~/.claude/output-styles/`
  were refused with "File is in a directory that is denied by your
  permission settings", with no prompt, and a `Write` to `~/.claude/scratch/`
  in the same turn succeeded. So the home-relative specifier binds the
  absolute path a tool is given. The rules still bind only the editing
  tools. **A Bash redirect is refused by the git-argv hook, not admitted:**
  `PROTECTED_TREES` holds `.claude` as a path component wherever it sits, the
  home directory included, so `>` into either directory was refused whether it
  was spelt absolute, `~/` or `$HOME/`. The same rule refuses
  `rm` under `~/.claude` too, so a probe file written to `scratch` is left for
  the owner to remove. **What is still open:** the hook judges only redirect
  targets and the operands of `WRITING_VERBS`, and that list trails the
  programs that write. `curl -o`, `tar -x` and an interpreter (`python -c`,
  `node -e`) are not on it, as its own comment says, so a Bash write into
  `memory/` or `output-styles/` by one of those is neither denied nor judged.
- **A command `/ship` runs before it pushes never denies push.** A
  frontmatter `disallowed-tools` holds for the rest of the user turn, not
  for the command that states it, so `Bash(git push:*)` on `/commit` or
  `/branch` refuses `/ship`'s own push a step later — "has been denied", in
  under a second, with no hook reason. It reads as hardening and is not:
  `/branch` and `/commit` leave the push to `/pr`, and the git-argv hook and
  `settings.json` refuse `main`, force and delete whoever asks. **Do not put
  it back.** `CHAINED_BEFORE_A_PUSH` in `test_harness_helpers.py` names the
  two and fails if one denies push again. A terminal, read-only command — the
  two sweeps — keeps its deny, because nothing pushes after it.
- **`/ship` grants exactly `Agent(branch-reviewer)`, for step 5's local review
  (#124).** The profile's `tools:` is `Read`, `Grep` and `Glob` — none of the
  broad types `/ship`'s `disallowed-tools` keeps from what it spawns — so the
  grant needs no hook, and the diff it reads sits under `artifacts/review/`,
  inside the checkout the profile is confined to. Because `allowed-tools`
  approves rather than whitelists, `/ship` and both sweeps deny every other
  profile by name; `CommandsEnforceTheEditingBoundariesTheyState` reads
  `.claude/agents/` on every run, so a new profile fails each exact grant that
  does not deny it, and fails if the granted one gains a tool beyond those three.
- **The external reviewers are archived, not deleted (#124).** The Grok and
  Copilot loops, `review-grok.md` and `review-copilot.md`, the adjudicator and
  triager profiles with their two `PreToolUse` hooks, the helpers and their
  suites, and `.claude/sandbox/` are at the tag `archive/external-reviewers`,
  taken from `main` before they were removed. `git checkout
  archive/external-reviewers -- <paths>` restores any of them, and the
  measurements that argued the triage's profile (#19, #23, #27) stand in this
  file at that tag. Restoring Grok needs a launcher outside the branch it
  reviews (#86 asked whether to build one); restoring either needs the grants
  and the deny list rebuilt with it, which is what that history is for.
  `guard-git-argv.py` dropped its rule for the Grok ledger and review runner
  with them (#160); restoring the helpers restores that rule in the same
  change.
- **`python .claude/scripts/shard-harness-suite.py` is the harness's own
  suite.** It reads `git ls-files`, so a new tracked root file or top-level
  tree fails it until somebody decides which side of the boundary it is on.
  Run it after any change under `.claude/`, `.github/locality-gate/` or
  `tools/`. Any Python 3.12 will do —
  `py -3.12` on Windows, `python` elsewhere — and CI's `harness` job runs
  `python` on three platforms.
- **That runner owns every discovery root, and deals their classes from a
  queue to parallel workers.** `harness-checks.sh` and the `harness` job name
  the runner and no root of their own, because two copies of a root is how a
  local check and CI come to run different suites. The unit is the class,
  since a class is what shares a fixture, and the count is the host's cores
  capped at the measured knee. A worker takes the next unit when it finishes
  the last, longest first, so a wrong guess at a class's cost costs that class
  rather than a whole worker. **What a splitter may never do is stop placing a
  class** — every worker green and one class never run is this repository's
  most-repeated failure in its cheapest form — so the units are compared back
  against discovery rather than against a list, by `test_harness_shards.py`,
  every unit has to come back finished, and a worker refuses an id discovery
  does not hold rather than reporting a pass.
  Two of its three tables are not about speed at all: `SERIAL` holds the
  classes whose subject is elapsed time or a listening port, `TOGETHER` the
  ones sharing a `secsweep-*` namespace in the temp root or this checkout's
  own `.git`, and a name in either that no longer resolves fails a test, so a
  renamed class loses its pinning loudly.
- **One run at a time, per user.** A run takes a lock named for the user in
  their temp directory before it discovers anything, and a second run, from
  another session or worktree, waits and says so. Two at once ran twice the
  workers the host holds and each took about three times as long (#323). The
  lock spans what one user runs, which on a developer's machine is every
  session that could collide; another user's runs, or one under a different
  `TMPDIR`, are not held by it. The operating system drops the lock with the
  process that held it, so there is nothing stale to clear.

- **The sweeps write two things outside `$work`, and both are fixed
  helpers' to write.** `sweep-slices.sh` cuts the pinned commit into lists of
  names in `$work.slices/`, a sibling of the worktree that
  `git-worktree-drop.sh` removes with it, so reading a list is the one
  exception to *every read is an absolute path under `$work`* and it exposes
  names, not the tree. `sweep-mark.sh` moves the local ref `refs/sweeps/<kind>`
  to a full commit sha with `git update-ref`, nothing else; it never pushes, and
  a checkout without the ref sweeps in full. Both take a closed kind, and the
  first refuses any path that is not a sweep-shaped direct child of the temp
  root, as the worktree helpers do. The residual is the ref: a wrong mark
  narrows the next sweep, and `full` is the answer. Their argument is in
  [`commands/bug-sweep.md`](commands/bug-sweep.md), *Slices, leads and the mark*.

- **The only force push in this repository is
  `.claude/scripts/git-rebase-onto-main.sh`, and `/ship` step 7 grants it by
  name.** Every `git push --force`, `--force-with-lease` and `-f` deny in
  `.claude/settings.json` is untouched, and `guard-git-argv.py` still judges
  every push written as a command; neither can see this one, because the push
  is inside the script rather than in a tool call. That is the design and not
  a gap: a permission pattern matches the text of a command, so it can pin a
  flag and cannot read a fact about the checkout, which is what the guards
  that matter in that script are — not the argument checks, which are text a
  rule can match and which `.claude/settings.json` already matches for `main`.
  They are enumerated in that script's header and nowhere else — not this
  bullet, and not the suite's docstring: three copies had drifted to three
  different lengths, and the shortest omitted the merge guard this bullet
  calls the one thing this repository reads differently. The
  guards are in the script and `test_git_rebase_onto_main.py` is what watches
  them: it pins the single leased push and the absence of every unleased
  spelling over the script's executable lines, runs most of the rest against
  real repositories, and keeps one case whose subject is the suite itself —
  that every class building the fixture also asserts the fixture was built,
  since a failed one makes those classes' assertions vacuous rather than
  red. Ported from `alexander-shamray/blueprint-backend`, which owns
  the argument. What differs here is that `/ship` step 0 reads ancestry rather
  than content, so the helper's merge guard is the only place in this
  repository that reads it.

- **A second helper rewrites published commits, and is a second force push.**
  `.claude/scripts/git-trailer-pr.sh` adds `PR: #<n>` to the commits of the
  branch checked out, which changes their SHAs, so it leases its push as the
  rebase helper does; `/pr` grants it by name and the `settings.json` denies
  are untouched. It takes one number and spells the `git rebase -x` command
  itself, so no `-x` grant stands beside it. It refuses a pull request that
  is not an open, same-repository one into `main` for this branch, a dirty
  tree, a remote that differs from the checkout in either direction, a merge
  commit, and a commit that already names another pull request. Its residual
  is a push that fails after the rewrite: the local branch then carries the
  trailers and origin's does not, and a rerun finishes it, because a commit
  that has the trailer is skipped. `git-revert-pr.sh` writes no remote; it
  reads trailers off `origin/main` and reverts on the current branch, and no
  command grants it yet. Ported from `alexander-shamray/blueprint-backend`
  (`blueprint-backend#1086`, PR 1098 there), which owns the argument; here the
  helper reads the pull request with a plain `gh pr view`, this tree having no
  bounded reader.
- **`/branch`'s worktrees sit inside the checkout, under
  `.claude/worktrees/`, and three residuals follow from it.** Once a session
  has entered one, `settings.json`'s denies anchor at the worktree and refuse
  its own `.claude/`; from the main checkout no rule names
  `.claude/worktrees/<slug>/.claude/`, and `guard-edit-target.py` and Claude
  Code's own sensitive-file check refuse it there instead — measured in
  `alexander-shamray/blueprint-backend`. `guard-git-argv.py` refuses a
  redirect, `cp`, `tee` or `mv` target with a `.claude` component, and every
  absolute path into a nested worktree has one, so from inside a worktree such
  a target is spelled relative; it fails closed and no chain step writes one.
  And `git-worktree-remove.sh` refuses a worktree forked beside the checkout
  before this layout, so one of those is removed by hand.
  **A relative write target is judged against where the session stands, and
  refused when the command moves it** (#359, #361). The event's `cwd` is joined
  to the target and the path from the checkout that contains it is judged, so
  after a bare `cd .claude/hooks` the next call's `cp x evil.sh` is refused. A
  command that changes directory with `cd`, `pushd`, `popd`, `source`, `eval`,
  the dot command, `env -C`, `--chdir`, `-S` or `--split-string` (clustered,
  attached or abbreviated; any word that is `env`, wherever it stands, and its
  option is looked for in every later word) or `find -execdir`/`-okdir` refuses
  every relative target in it. `env X=1 ls -S`, `env -uC`, `echo env -C` and a
  path with a component named env (`grep foo app/env/x -C3`)
  therefore read as a directory change: over-matching is the direction chosen,
  because every attempt to ask where `env` stands failed open in review.
  **Not seen:** a directory change by a program not named here (`make -C`,
  `tar -C`, `sudo -D`, `systemd-run --working-directory`, `docker run -w`),
  which are not on the writing-verb list either; an `env` or `cd` word built
  by a substitution (`$(printf env) -C d`), the class `$(printf cd)` already
  is.
  **On Windows `git worktree remove` can unregister a worktree and then fail to delete its
  tree** (`Permission denied` or `Invalid argument`): measured, a process held
  `<worktree>/.claude` for about 40 s after a `dotnet build` there, build servers shut
  down or not, and python removed the same tree once it let go. Both removal helpers
  therefore ask the register again when git fails: still registered is git's refusal and
  stands; unregistered, established twice (a listing that runs and omits the path, and git no
  longer recognising the directory), leaves only a directory, which is deleted for a
  bounded time (the tries and pause are the helpers' `CLAUDE_WORKTREE_REMOVE_TRIES` and
  `_PAUSE`) and reported, and the sweep helper's `.slices` lists go with it (#329);
  any doubt keeps the tree and git's own exit status. A directory still held after that is named for deletion by hand.
- **The edit guard judges the main checkout from a worktree, and refuses a
  trailing dot or space on Windows.** A session standing in a linked worktree
  gets `protected_in_worktree`'s inventory applied to any main checkout the
  target sits in (`.git/`, `CLAUDE.md`, `.github/`, `.claude/`, the toolchain
  files), because once the session is in the worktree `settings.json`'s denies
  anchor there and name nothing in main; whether the harness re-roots the
  project is unmeasured, so the guard does not wait for the answer. Targets in
  the worktree the session stands in, in another linked worktree, and ordinary
  files of main are judged as before. On Windows a path component ending in `.`
  or a space is refused outright, since `abspath` and `realpath` drop it and
  whether the rule matcher does is unmeasured (a link-style refusal already
  caught an existing file spelled so; a new name such as `docs.` was admitted).
- **`find` is judged by its writing actions only.** `guard-git-argv.py` reads
  `-fprint`, `-fprint0`, `-fprintf`, `-fls` and `-delete`. An action word that is
  itself computed (`find /tmp "$A"`) is not read, and a `-delete` start given as
  a POSIX-style absolute path on a Windows host (`/c/dev/...`) is not resolved
  against the working directory, so only the relative spellings of a parent are
  refused there. `rm`, `rmdir`, `shred`, `unlink` and `mv` are judged the same
  way (#309): an operand that resolves to the working directory, a parent of it,
  `/` or the home directory is refused (`rm -rf ..`, `mv .. /tmp/x`), as is
  `mv`'s source but not its destination. A sibling (`rm -rf ../old`) and a
  destination of `.` or `..` are judged only as any other path. The same
  POSIX-style gap holds there.
- **The git-argv hook answers for a run that does not finish, and for an event
  it decodes itself.** A hook that times out is non-blocking, so
  `guard-git-argv.py` refuses what it would not finish judging (#362, #363,
  #360). A brace expansion that makes more than `BRACE_WORD_LIMIT` words
  (`{,}` thirty times is 2^30) is refused, as is a command past
  `SCAN_BUDGET` or `LENGTH_BUDGET`; a watchdog thread answers with a refusal
  after `WATCHDOG_SECONDS` and ends the process, which covers a Python loop that
  runs long and not a single regular-expression match, which holds the
  interpreter until it returns and is bounded by the two budgets instead.
  Within them a pattern may still grow with the square of the line, so the
  two that grew faster are not patterns: arithmetic and a git writer line are
  each read in one pass (`expansion_writes`, `git_writer_line`), and every
  shape measured on #447 answers within a few seconds at `LENGTH_BUDGET`,
  where `[[` repeated took 40 s and 24,000 `git` words 173 s. The event is read as
  bytes and decoded as UTF-8 with replacement, never with the locale's code
  page, which on Windows raised on `Á` and let the handler allow the event
  unjudged. A malformed event that is not JSON is still allowed, and says so on
  stderr. A forbidden git flag is looked for in every word a brace list or a
  single-letter or numeric range, stepped or not, can make, expanded until
  nothing changes, so `--{oneline,output=/tmp/y}` is refused. A range bash
  leaves alone (`{a..3}`, `{a..b..c}`) is judged as written, and a range that
  would make more than the word limit is refused.
- **`sed` is judged by reading its script, and a word the shell expands is
  refused.** `guard-git-argv.py` reads `w`, `W`, `s///w`, `e` and `s///e` in a
  `-e` or first-operand script, refuses `-f`, and refuses any `sed` word with
  an expanding `$` or a backtick outside single quotes, or an unquoted brace
  group holding a comma or `..`, since an expanded word can be an option or a
  second script. The cost is that `sed -n 1p "$FILE"` is refused
  until the words are named literally; operands after a literal `--` are
  exempt once a script is given. Labels end at a blank as GNU sed 4.9 reads
  them, so a script an older sed reads differently is refused rather than
  admitted.
- **The files MSBuild and NuGet load without being asked are denied by name.**
  `Directory.Build.targets` and `.rsp`, `Directory.Solution.props` and
  `.targets`, `NuGet.Config` in each casing, and the solution's
  `before.`/`after.`/plain `.slnx.targets` are untracked, so no listing of
  tracked files reaches them, yet an `Exec` in one runs under the granted
  `host-checks.sh` and the build reports success (#311). Each editing command and
  `settings.json` deny them at the root in both spellings and by family at any
  depth (`MSBuild.rsp` and any `global.json` among them), and `PROTECTED_FILES` carries the names, so a redirect to one is refused
  at any depth too. Beside them: `Directory.Packages.*`, `*.csproj.user` (the SDK's
  common targets import it) and any `obj` tree, where a restore that finds nothing
  changed does not rewrite a planted `*.nuget.g.targets`. The suite holds the list as
  the Node one is held. Not covered: a `Directory.Build.props` above the checkout,
  which the edit guard's outside-the-checkout rule judges.
- **`fork` is denied beside the built-in agent types, and no other agent type is
  named.** `Agent(fork)` joins the broad types `/ship` and the two sweeps deny, since
  a fork inherits the parent's whole toolset. `allowed-tools` auto-approves rather
  than whitelists, so a user-level or plugin agent type installed on a machine is
  outside the boundary: no deny list in this tree can name a type it does not
  know, and the suite asserts the fixed built-in list plus the repository's own
  profiles (#315).
- **`/style-pass` edits `eslint.config.js` and then runs the linter it
  configures.** That is the pass's job (sections 3 and 4), so the one executed config
  stays editable and the check helper stays granted; the other executed configs
  (`vite`, `vitest`, `playwright`, `prettier`, `jest` and `karma` configs by name, `*.config.cjs|mjs`, `.prettierrc.js|cjs|mjs`) at any depth are denied (#313); `*.config.ts` is not denied here as a family, since Angular's tracked `app.config.ts` is a providers file. A
  session that writes a hostile `eslint.config.js` and runs `npm-checks.sh lint`
  executes it, the same class as session-written tests under #310; the review
  of the diff, not a rule, is what stands between them.
- **The global deny list names `.claude/rules/**`, `.claude/output-styles/**` and
  `CLAUDE.local.md`, and not `CLAUDE.md`.** The first three are loaded into the session
  unasked and nothing here writes them. `CLAUDE.md` is denied by each command that edits
  and left writable globally, because `/style-pass` (sections 3 and 4) and the owner's own
  sessions edit it by design; a global deny would refuse them (#343). The rebase helper's
  force push names both sides of its refspec (`<branch>:refs/heads/<branch>`), so
  `remote.origin.push` has no ref left to decide. It refuses (exit 7) when `origin`
  has more than one push URL, or when the URL git will push to, after `pushurl`,
  `pushInsteadOf` and `insteadOf`, names a different host or path than the URL
  the remote was added with (`repo_key`: lower-cased host and the whole path, so a
  deeper `evil/o/r` is another key; a dot or empty path segment has none); a protocol rewrite on the same host (https to ssh) passes. A mirror on
  another host is refused, as is a one-component path, a URL carrying a query or
  fragment, and anything with a scheme or an scp-style colon that fits neither form
  (`ext::`, `file://`, `ssh://h:o/r`, an IPv6 host); only a plain or drive path may be
  hostless; GitHub URLs always carry an owner, so those fail closed on a setup this
  repository does not have. The lease (the remote's branch must already be at the commit
  this run read) backs that up and does not replace it.
- **`git restore`, `git checkout` and `git clean` are judged by the paths
  they write.** `guard-git-argv.py` reads the subcommand and its operands: a
  pathspec on a protected surface, `.`, a parent of the checkout, a glob, a
  `:` magic pathspec, a path list read from a file and a bare `git clean -f`
  (which means the whole working directory) are refused, as is any of them
  under `-C`, `--work-tree` or `--git-dir`. A branch checkout, `git restore
  --staged` alone, `git clean -n`, `git checkout HEAD -- <path>` and `git
  restore --source=HEAD <path>` (the committed copy, which is how this
  repository restores a file) are admitted; any other tree-ish brings in
  content nobody reviewed on this branch, and `git checkout origin/main --
  .claude/settings.json` is refused. A path-writing subcommand must be led by
  `git`: behind `xargs`, `find -exec`, `env -C` or `sudo` its paths can arrive
  elsewhere or its directory can move, so it is refused. A work tree
  relocated by `GIT_WORK_TREE`, `GIT_DIR`, `GIT_COMMON_DIR`, `GIT_INDEX_FILE`
  or `GIT_CONFIG*` assigned anywhere in the command line (an `export` before
  the git word included), or by `-c core.worktree=`, is refused too; one
  exported by an earlier Bash call is not seen. **`git rm` and `git mv` are
  held to the same relocation refusal** (#358): `verb_offence` judges their
  operands as the verbs `rm` and `mv` and sees only the words after the verb,
  so `git -C .claude rm -f hooks/guard-git-argv.py` had no `.claude` component
  to refuse. Run from the checkout their operands are judged there, as before.
  The cost is an over-refusal. The whole-command scan also refuses `git rm`
  or `git mv` behind a wrapper (`xargs git rm`). And every writer this bullet
  judges is refused beside a bare `eval`, `source`, `trap`, `alias` or
  `enable` word (`git mv old.txt source`, `git checkout -b enable`), and
  beside any export route the relocation paragraph below lists, read across
  the event's whole line (`set -a; git checkout main`, `bash -c 'ls -la' &&
  git checkout main`). A branch checkout is included, because switching
  branches under a moved work tree writes the whole tree there.
  Discarding edits in an application tree with `git restore` or `git checkout
  --` is refused as well, which is the rule rather than an oversight.
  A `git checkout` with no `--` cannot tell a branch from a path, so an
  application tree such as `docs/` counts there only for a name that exists,
  ends in a dot and a letter (`x.ts`, not `spec-v1.2`) or is **no ref**: a name
  that is no branch, tag or remote branch, read from the repository's files
  and not by running `git`, cannot be what git would switch to, so `git
  checkout tools/Makefile` is a path whether or not the file is there, and
  `docs/spec-v1.2` is admitted when a branch of that name exists. Outside a
  repository every name is read as a ref; a repository that cannot be read, as
  none. A lone `$BRANCH` the command never assigns is the environment's
  and admitted; one the command sets (`F=.claude/x; git checkout $F`), or any
  other expansion, is judged as a path; so is a name the shell manages
  (`$PWD`, `$REPLY`, `$_`) or one whose inherited value is absolute or holds
  `..` (`BRANCH=chore/x` is a branch). A bare `$CLAUDE_PROJECT_DIR`, `$HOME`,
  `$TEMP`, `$TMP` or `$TMPDIR` as a pathspec is the checkout or holds it, and
  is refused. Refused whatever the operands, because the paths cannot be read
  from the command: a command-substitution word, `-C`, `--work-tree`,
  `GIT_CONFIG*`, a wrapper (`command`, `nohup`, `xargs`, `sudo`), and an
  option git does not list among its global ones ahead of the verb, which
  takes the verb's place (`--super-prefix x/ checkout`); `--no-pager` is
  known and leaves `git --no-pager log --grep checkout` alone. After a `cd`,
  every operand of an unseparated checkout with several operands is judged,
  and a lone operand with a `.` or `/` or that is no ref (`cd .claude && git
  checkout settings.json`); a lone one that is a ref is a branch. A
  relocation variable named anywhere in the command line, or
  built by `eval`, `source`, `trap`, a lone `.` followed by a word (`. file`,
  after any prefix; a redirection and its target between them do not count,
  so `git add . > log` is not one), a process substitution, a command
  substitution, or an expansion in the **name** of an argument of `export`,
  `declare`, `typeset`, `local`, `readonly`, `env`, `read`, `mapfile` or
  `readarray` (`export X=$Y` builds a value, not a name), or a brace, glob or
  quote in any such argument, or `printf -v`, `getopts` or `wait -p` with one,
  refuses the writer. The message of a commit (`-m`, `-am`, `--message`)
  given as one quoted string that expands nothing is text and is not read, so
  `git commit -m "fix GIT_DIR handling"` beside a writer is admitted. The `.`
  that changes directory is the command only where a command stands; as an
  operand (`git add .`) it is a directory. **The relocation is closed by the
  route that exports it, not by its spelling (#357).** A hook cannot ask git
  where it will write: the environment the command builds does not exist
  until the command runs. A name reaches git only once it is exported, though,
  and bash exports by a closed list of routes: an assignment ahead of a
  command, `export` and `declare -x`, `allexport`, a nameref an `export` then
  follows, and code read from data. A program that sets its child's
  environment is a further route, and no list of launchers closes it, so the
  list is of the wrappers that add nothing (`INERT_WRAPPERS`: `nohup`,
  `timeout 5`, `nice -n 10`, `command`, `env`; `env`'s `-C`, `-S`,
  quoted words and expansions are refused by its own rules, so what is left
  to it is literal assignments).
  Beside a writer, a shell any other program starts is refused (`envdir
  /tmp/d bash -c '...'`, `chpst -e`, `xargs bash`, `sudo bash`, by `-c`, a
  here-string, a heredoc or a pipe, and through a shell between them), and so
  is a program `EVALUATORS` does not name handed a script that runs git, whose
  startup files this guard cannot know (#421, #397). A lookup (`which bash`),
  a reader, `gh` and an interpreter start no shell here. Each of bash's
  routes that can export a name this guard cannot read is refused too, and so
  are three programs: an expansion anywhere in the
  words of `env`, `sudo` or `doas` (path-qualified too), or of a declaration
  builtin behind `command` or
  `builtin`, which split `X=$Y` into a second name (`export X=$Y` alone does
  not, and stays admitted); any nameref, which a `for` loop can point at a
  computed name; `set -a`, `set -o allexport`, `shopt -o`, `SHELLOPTS`, a
  `set` or `shopt` word holding an expansion, quote, brace or glob, and a
  shell started with `-a`; and `BASH_ENV`, `ENV`, a shell started
  interactive or login (`-i`, `-l`, `--login`, `--rcfile`, `--init-file`
  anywhere in that shell's command, past any redirection, quoted span or
  escape, and an option word holding a quote, backslash or `$` or any
  unquoted expansion there, which fails closed and so also refuses `bash -c
  'ls -la'`; and `exec -l` or `exec -a -bash`), `zsh` and `ZDOTDIR`, `fish`,
  `csh` and `tcsh`, since each reads a startup file on every start,
  `alias`, `enable` and `mapfile -C`,
  which run code. A computed name
  that is only bound (`read`, `printf -v`, `(( $N = 5 ))`) is exported by
  none of these and reaches no git process. A script a shell runs (`bash -c`,
  `eval`, a here-string, `$(...)`) is judged with every scope around it,
  where its environment is built: the event's line as bash performs it
  (continuations joined, comments and heredoc bodies left as data), and every
  script that line hands a shell on stdin, which is code (a `git commit -F -`
  body stays data). Until #357, `GIT_WORK_TREE=.claude bash -c 'git checkout
  HEAD -- settings.json'` was admitted. Still not seen: a variable already
  in the environment this command line starts with, inherited by the session
  or exported by the profile the shell sources, which relocates a plain `git
  checkout -- settings.json` as well; an interpreter's code (`python -c`
  setting `os.environ` and starting `bash -c`), which this guard does not
  read as a command line, the write residual above; what an unclassified
  program **named bare, and not a shell this file knows**, reads on stdin when
  the text is not on the line (a file, a command's output, an expansion's
  value: `x='git push …'; echo "$x" | awk '{system($0)}'`), unless a later
  word on its run names a shell or an extensionless path (`sudo pwsh`,
  `flock l /tmp/x`); such a program may run what it reads (`xargs`, `awk`), and
  only text on the line is in the source to judge; a reader on the
  list whose own config makes it run its input (a `git` alias beginning
  `!`); and `awk`'s own program, which is code as Python's is
  (`gawk '{system("git …")}'`, the interpreter residual on #424). **The stdin
  channel is judged by content** (#397). A program is classified when it is
  a shell `EVALUATORS` names that reads its stdin as its script (not one
  given `-c`, whose script inherits that stdin: `bash -c pwsh <<<…`), or a
  reader or interpreter named bare
  (`stdin_classified`: `READING_COMMANDS`, `STDIN_DATA_READERS`, the
  interpreters, the printers; after a reserved word such as `do` or `while`
  the next word is the program). Any other is refused a heredoc body, a
  here-string, a printer's text earlier in its pipe, a process
  substitution's body, an unseen subshell's line or a transforming stage's
  own words that are computed (`$(`, a backtick, `$'`, or a printer whose
  output this file cannot reproduce, such as `printf` with a `%` or a
  backslash escape, or `echo` with any backslash, which `dash` and macOS
  `sh` decode unasked; a quoted heredoc body is computed by no outer shell, so
  `gh issue comment 1 --body-file - <<'EOF'` with a code span is admitted)
  or hold a line that runs git to write
  once read as typed, continuations joined, or with its parameter expansions
  removed (`git_writer_line`, `GIT_STDIN_WRITERS` among it), so `echo
  'git push origin +HEAD:main' | /tmp/x` and `/tmp/x <<< 'git checkout HEAD
  -- settings.json'` are refused. **A program that must be taken for a shell
  is held strictly**: one named by a path, since a copy names it anything
  (`cp /bin/bash /tmp/sort`); any name on a line that changes what a bare
  name runs (a `PATH` write by any builtin, `hash`, `alias`, `enable`,
  `source` or `.`, read as bash reads a command word, or a function
  definition); and a shell by a name `EVALUATORS` does not read
  (`FOREIGN_SHELLS`: `pwsh`, `cmd`, `fish`, `nu` and others), or a shell of
  either list under a packaged or versioned name (`ksh93`, `bash-static`:
  `SHELL_VARIANT`). Reserved words
  are read through, `eval`, `trap`, `fc` and `mapfile -C` count because each
  runs a string, and any text bash expands that names `PATH`, `BASH_CMDS` or
  `BASH_ALIASES` counts however it is spelt (`for PATH in`, `${PATH:=…}`,
  `printf -vPATH`, `BASH_CMDS[tee]=`), quoted text never (`grep 'PATH='`).
  So do a writer's target built by an expansion, a nameref, an integer
  attribute, `let`, and arithmetic that reads a variable (`$((x))`, a
  subscript, a name operand's subscript such as `unset a[x]` or `wait -p`'s,
  a name operand from an expansion such as `test -v "$n"` or `wait -fp"$n"`,
  an indirect `${!n}`, a `${…@…}` transformation, a compound assignment's
  subscript, a substring offset, a prompt string or `set -x`, which
  prompt-expands `PS4`), since arithmetic evaluates a value
  holding `PATH=0` as an assignment. **What stays open there is bounded by
  #424**: a route this list does not name, such as an unquoted expansion,
  a glob or a brace expansion that word-splits into a `test` operator, or a
  command word that comes from an expansion. Closing every route needs a
  bash interpreter, not an argv guard. It would also buy nothing past what
  an interpreter on the line already does unjudged (`python -c` running
  git), which #424 accepts. Such a program
  is handed plain words only: stdin text holding a quote, an escape, an
  expansion, a bracket, a join or a glob character (`UNPLAIN`) is refused
  unread, since a shell builds a word from those pieces (`g=gi; ${g}t push`,
  PowerShell's `& ("gi"+"t") push`, cmd's `g^it push`), as is any `git`
  word, and so is any stdin that is not on the line: a file other than
  `/dev/null`, its own or a group's (`{ …; } < f`,
  `done < f`, `exec < f`), a subshell the walk does not see, a coprocess,
  which reads what the line writes to its descriptors (`coproc pwsh; echo …
  >&63`), or a stage that transforms (`base64 -d <<<… | pwsh`). A pipe reaches its reader across a
  newline and into a subshell, and a resolution change counts from any
  scope around a nested script (`PATH=/tmp bash -c …`). A `>(…)` starting an
  unclassified program refuses a line with a `git` word on it. Reading every
  heredoc handed to an unnamed program as a shell script would refuse an
  ordinary `py - <<'EOF'` whose Python holds a backtick, so a listed
  reader's stdin is not judged; `xargs`, `less`, `gh`, `awk` and `read` are
  on no list, since a program can run what each reads (`awk '{system($0)}'`,
  `while read l; do $l; done`), while `sed` is, because the `sed` script
  pass refuses every script that runs its input. Replayed over the 4000 most
  recent distinct commands of this repository's session transcripts, the
  rule changed three verdicts: an environment-variable probe that declares a
  nameref, and two issue bodies handed to a function defined on the same
  line, which makes every name as good as a path and Markdown no plain text
  (calling the filing helper directly is admitted). Refusing every pipe into
  a program on no list would have changed 164. One shape is held strictly at a cost that measured nothing there: a
  line holding `wait $pid` and a pipe into a reader (`wait $!` is admitted).
  **A shell is a program, not a name** (#397):
  `EVALUATORS` holds the POSIX family by each name it ships under (`ash`,
  `mksh`, `yash`, `posh`, `rbash` among them), and the word after any
  program's script flag (`-c` and its bundles, `cmd`'s `/c` and `/k`, `-Command`)
  is judged as a command line, so `cp /bin/bash /tmp/x; /tmp/x -c 'git push
  origin +HEAD:main'` is refused; a reader and an interpreter are left out
  (`grep -c`, `python -c`, `python3.12 -c`, `pythonw -c`; an interpreter
  behind a launcher such as `uv run` is not). Replayed over the 4000 most recent distinct
  commands of this repository's session transcripts, that refused one the old
  hook admitted: a PowerShell `-Command` whose `$_` stands where bash reads a
  program name.
  `git checkout -f`, `git reset --hard`, `git stash`, `git checkout-index`,
  `git read-tree -u` and the subcommands that merge or replay (`merge`,
  `cherry-pick`, `rebase`) update the tree wholesale or by index and are not
  judged.

A new residual is stated **here**, and `CLAUDE.md` carries the pointer rather
than a second copy.
