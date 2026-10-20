# Harness boundaries

**What the agent harness grants this repository's commands, what it refuses
them, and every place a grant is wider than the operation it was added for.**
Load-bearing whenever you touch `.claude/` — a command's frontmatter, a
helper, `settings.json`, a hook — and inert for every other kind of change.

**This harness was ported from `alexander-shamray/blueprint-frontend`, which
ported it from `alexander-shamray/blueprint-backend`.** A bare `#NN` anywhere
under `.claude/` or in a copied helper comment is an issue in **that**
repository, not one here. The arguments for the grants, the deny lists, the
git-argv hook and the edit-target hook live in those two
`docs/harness-boundaries.md` files and are not restated here; a summary of an
argument is how a rule gets "corrected" back.

**Index — grep this file for the bold lead-in, never read it whole.** Each
line names one residual below by the words it opens with.

| Touching | Grep for |
|---|---|
| The check helpers, e2e | `Two toolchains` |
| `settings.json` or a permission rule | `File permission rules take`, `self-locks` |
| A guard hook or its launcher | `Every guard hook uses` |
| The index: refresh, query or search hints, MCP approval, timeout | `Two hooks guard nothing`, `The second is the query hint`, `The search hint asks`, `A session started in a worktree`, `The CLI's skill rewrite`, `The MCP startup timeout` |
| A command's `allowed-tools` or `disallowed-tools` | `grant the index commands`, `runs before it pushes never denies push`, `is a grant surface` |
| The review agent grant, or restoring an external reviewer | `grants exactly`, `archive/external-reviewers` |
| The harness suite and its shards | `shard-harness-suite.py`, `That runner owns every discovery root` |
| The sweeps' slice lists and `refs/sweeps/` | `The sweeps write two things` |
| Rebasing or force-pushing | `The only force push`, `A second helper rewrites` |
| `/branch` worktrees under `.claude/worktrees/` | `worktrees sit inside the checkout` |

A new residual is stated **here** and gets a row above.

**What this repository adds, and is therefore the only residual stated here:**

- Two toolchains. `/ship` and `/review-branch` run
  `bash .claude/scripts/host-checks.sh` (`dotnet test BlueprintAdmin.slnx`)
  and `bash .claude/scripts/npm-checks.sh` (lint / test / build **inside**
  `src/Admin.Web`). There is no `e2e` mode: Playwright needs the Host under
  FakePlatform, and a suite that passed because nothing was listening would
  be worse than none. CI's `smoke` job is that check.
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
  the owner to remove.
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
- **That runner owns every discovery root, and runs their classes as parallel
  workers.** `harness-checks.sh` and the `harness` job name the runner and no
  root of their own, because two copies of a root is how a local check and CI
  come to run different suites. The unit is the class, since a class is what
  shares a fixture, and the count is the host's cores capped at the measured
  knee. **What a splitter may never do is stop placing a class** — every
  worker green and one class never run is this repository's most-repeated
  failure in its cheapest form — so the placement is compared back against
  discovery rather than against a list, by `test_harness_shards.py`, and a
  worker refuses an id discovery does not hold rather than reporting a pass.
  Two of its three tables are not about speed at all: `SERIAL` holds the
  classes whose subject is elapsed time or a listening port, `TOGETHER` the
  ones sharing a `secsweep-*` namespace in the temp root or this checkout's
  own `.git`, and a name in either that no longer resolves fails a test, so a
  renamed class loses its pinning loudly.

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
  (#1086, PR 1098), which owns the argument; here the helper reads the pull
  request with a plain `gh pr view`, this tree having no bounded reader.
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

A new residual is stated **here**, and `CLAUDE.md` carries the pointer rather
than a second copy.
