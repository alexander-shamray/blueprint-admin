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
- **The two hooks use `run-guard.sh`**, which locates a compatible Python
  launcher before invoking the guard.
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
  above never happens there. Two rules cannot live in a profile: a type
  list inside a subagent's `Agent` grant is ignored, and a path in a
  profile's `disallowedTools` removes the whole tool. Both are therefore
  in `/ship`'s own `disallowed-tools` — the broad agent types, and every
  `Edit(...)` `/review-grok` states — which reaches the agents a command
  spawns, as `review-grok.md` records of its adjudicator.
  `CommandsEnforceTheEditingBoundariesTheyState` pins the profile, the
  grant and both deny lists, and reads `.claude/agents/` on every run, so
  a new profile fails each exact grant that does not deny it. It pins no
  runtime behaviour, and that a profile's `tools:` holds is Claude Code's
  documented allowlist rather than a measurement here: profiles register
  at session start, so the session that wrote this one could not spawn
  it. Spawn it and read its tool list when Grok is re-enabled.
- **`python -m unittest discover -s .claude/scripts -p 'test_*.py'` is the
  harness's own suite.** It reads `git ls-files`, so a new tracked root file
  or top-level tree fails it until somebody decides which side of the
  boundary it is on. Run it after any change under `.claude/`. Any Python
  3.12 will do — `py -3.12` on Windows, `python` elsewhere — and CI's
  `harness` job runs `python` on three platforms.

A new residual is stated **here**, and `CLAUDE.md` carries the pointer rather
than a second copy.
