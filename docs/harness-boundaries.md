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
- **Every guard hook uses `run-guard.sh`** — the two in `settings.json` and
  the `review-grok-triager` profile's own — which locates a compatible Python
  launcher before invoking the guard. **It pays that probe once per host
  rather than once per guarded call**, remembering the winner under
  `.claude/cache/`, which `.gitignore` covers whole. Every condition that
  validates the mark is a shell builtin, so the remembered path forks nothing;
  the mark is checked against the `PATH` it was chosen under and against the
  interpreter's own executable, because a mark believed wrongly ends in a
  failed `exec`, and a `PreToolUse` hook that fails any way but exit 2 lets
  the tool run unguarded. **The mark names one of four spellings and can name
  nothing else**: no `Edit(...)` deny covers `.claude/cache/`, so without that
  closed set a file the guarded session may write would be a command run
  before every guarded call. `TheLauncherPaysTheProbeOncePerHost` drives a
  copy of the launcher through each of those failures.
- **The one hook that guards nothing is the index refresh**, which runs
  `refresh-index.sh` from two events: a `PostToolUse` entry after every tool
  that writes, and a `SessionStart` entry for the changes no edit makes. A
  merge, a switch or a pull rewrites the tree with no tool event behind it, so
  a session opening onto one of those would otherwise read an index describing
  the tree it replaced. One command serves both, because the script resolves
  its repository with `git rev-parse` from the working directory and reads no
  event payload at all — neither entry has anything to pass it. Each
  backgrounds and silences it so it can never block an edit, which also means
  a broken spelling fails on every call without a sound — the example it
  replaced passed a `--quiet` the CLI does not have. The script coalesces
  overlapping calls so an `update` always starts after the last edit, and
  retries a failed one. `test_index_refresh_hook.py` runs it against a fake
  CLI, and it pins the two events, the matcher and the
  `CBX_NO_SKILL_AUTO_UPDATE` guard `.mcp.json` sets, in the script and in the
  skill's `examples/hooks/settings.json` alike — the events by their whole
  set, so a third added to either file without a test of its own is a red case
  rather than a silent one. The example stays a self-contained one-liner,
  because the script is this repository's and not the skill's. Both
  call the CLI bare, as `.mcp.json` does, rather than through the skill's
  `cbx` wrapper: a hook shell's `bash` can resolve to WSL's on Windows, which
  cannot run it.
- **`.claude/skills/**` is a grant surface.** A skill's `allowed-tools` is
  auto-approval, so a session that can rewrite `SKILL.md` widens the next
  invocation. Commands, agents, hooks and settings were already denied;
  both `Edit(.claude/skills/**)` spellings sit on the same list.
- **A command `/ship` runs before it pushes never denies push.** A
  frontmatter `disallowed-tools` holds for the rest of the user turn, not
  for the command that states it, so `Bash(git push:*)` on `/commit`,
  `/branch` or `/review-copilot` refuses `/ship`'s own push a step later —
  "has been denied", in under a second, with no hook reason. It reads as
  hardening and is not: `/branch` and `/commit` leave the push to `/pr`,
  `/review-copilot` pushes only an already-committed review fix, by name,
  and the git-argv hook and `settings.json` refuse `main`, force and delete
  whoever asks. **Do not put it back.** `CHAINED_BEFORE_A_PUSH` in
  `test_grok_helpers.py` names the three and fails if one denies push
  again. A terminal, read-only command — the two sweeps — keeps its deny,
  because nothing pushes after it.
- **`/review-grok` is the one chained command that cannot follow that rule,
  so `/ship` step 5 runs it inside an `Agent` instead.** Run inline, its
  bare `Bash` deny is its boundary — it reads an untrusted review holding
  `Edit` — and under the same turn-wide lifetime it would also refuse every
  command step 5 runs after it: the checks, `/commit` and the push. The
  deny stays and the triage moves.
- **On the one agent type measured, the agent keeps the deny off the push
  by discarding it, so the agent alone is not the boundary.** Measured:
  `/review-grok` loaded through the Skill tool in the main session removed
  `Bash` until the next user message — background notifications did not
  end it — while the same load inside a `general-purpose` agent left `Bash`
  working there, and the parent's `Bash` in the same turn was unaffected
  (#19). So on that path the push is safe and the triage would read an
  untrusted review holding a shell.
- **So the triage runs under a profile of its own, and `/ship` says what a
  profile cannot.** Step 5 grants exactly `Agent(review-grok-triager)`,
  whose `tools:` — an allowlist — holds no `Bash` and no `Skill`; it reads
  `review-grok.md` rather than loading it, so the skill load measured
  above never happens there. Two rules cannot live in `tools:`. A type
  list inside a subagent's `Agent` grant is ignored, so the profile's own
  `PreToolUse` hook, `guard-triager-dispatch.py`, admits `review-adjudicator`
  and refuses every other dispatch — the triager itself included, which
  `/ship` grants and so cannot deny. And a path in a profile's
  `disallowedTools` removes the whole tool, so every `Edit(...)`
  `/review-grok` states is in `/ship`'s own `disallowed-tools`, beside the
  broad agent types; that list reaches the agents a command spawns, as
  `review-grok.md` records of its adjudicator — in the turn it was loaded
  in, which is why the trees also have a hook (below).
  `CommandsEnforceTheEditingBoundariesTheyState` pins the profile, the
  grant and both deny lists, and reads `.claude/agents/` on every run, so
  a new profile fails each exact grant that does not deny it;
  `TheTriagerDispatchesOnlyTheAdjudicator` runs the hook through the
  launcher and pins its wiring on the profile. Neither pins runtime
  behaviour, so it was measured (#23), by spawning the triager and
  probing it. **Two properties hold:** its tools were exactly `Read`,
  `Grep`, `Glob`, `Edit`, `Write` and `Agent`, with no shell and no
  `Skill`; and the hook refused `general-purpose` and
  `review-grok-triager` by name and let `review-adjudicator` run.
- **The third held only in the turn `/ship` was loaded in (#27), so it
  moved onto the profile.** Spawned in that turn, the triager was refused
  an `Edit` to `.github/**` and `README.md` — *File is in a directory that
  is denied by your permission settings* — while a `docs/**` control
  passed. Spawned one user message later, without `/ship` re-invoked, it
  wrote into `.github/` and edited `README.md`: the turn-wide lifetime
  stated above, reaching the one boundary that rested on it. Step 5 spawns
  the triager async, so every round after the first starts in such a turn.
  The profile now carries a second `PreToolUse` hook,
  `guard-triager-edit.py`, on `Edit|Write|MultiEdit|NotebookEdit`: it reads
  `/ship`'s `Edit(...)` denies from `ship.md` on every call — one list, no
  copy — and refuses a target under any of them, matched without regard to
  case, and any target in no checkout. `/ship`'s list still states the
  trees; the hook is what makes them hold outside that turn.
  `TheTriagerEditsNothingShipDenies` runs it through the launcher against
  every pattern that list holds and pins its wiring on the profile.
  **Unmeasured at runtime:** that this hook fires on `Edit` in a turn after
  `/ship`'s rests on the profile-hook mechanism the dispatch hook was
  measured under, not on a probe of this one. The measurement owed before
  Grok returns: spawn the triager in a turn after the one `/ship` was
  loaded in, have it edit `README.md`, and see the hook refuse it. The
  guard also refuses any target outside the checkout its event's `cwd`
  stands in, so a sibling worktree or another repository is out of reach.
- **`python .claude/scripts/shard-harness-suite.py` is the harness's own
  suite.** It reads `git ls-files`, so a new tracked root file or top-level
  tree fails it until somebody decides which side of the boundary it is on.
  Run it after any change under `.claude/`. Any Python 3.12 will do —
  `py -3.12` on Windows, `python` elsewhere — and CI's `harness` job runs
  `python` on three platforms.
- **That runner owns both discovery roots, and runs their classes as parallel
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

A new residual is stated **here**, and `CLAUDE.md` carries the pointer rather
than a second copy.
