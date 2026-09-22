"""git-rebase-onto-main.sh: the only force push, and what bounds it.

A permission rule matches the text of a command, so it can pin a flag and
cannot pin a fact about the checkout. The guards that make this force push
safe are mostly the second kind, and `git-rebase-onto-main.sh`'s own header
enumerates them — not this docstring, which used to carry a shorter list of
its own and is how two of them stopped being mentioned anywhere. The script
is the boundary and this suite is what watches it. The first class reads the
flags, the last reads this file, and the rest run the thing against real
repositories.
"""

import ast
import os
import re
import shutil
import subprocess
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HELPER = SCRIPTS / "git-rebase-onto-main.sh"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where either of these may be WSL's.
#
# They are pinned by different means and both have to be. `bash` is an argv
# element, so the path is the whole of it. `git` is never run by Python at
# all — every git call below is made by bash, from inside a fragment — so
# pinning it means putting its directory first on the PATH that bash gets,
# which `run_bash` does. Resolving `GIT` and then not using it left the
# probe in `setUpModule` checking one git while the fixtures ran another.
BASH = shutil.which("bash")
GIT = shutil.which("git")


def setUpModule():
    # Not a skip. `test_grok_helpers.py` owns that argument for the same two
    # tools: a skip on a missing tool reports a pass, and this suite would then
    # have established nothing about the only force push in the repository.
    missing = [name for name, path in (("bash", BASH), ("git", GIT))
               if path is None]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests run "
            "the script rather than a Python equivalent of it"
        )


def run_bash(script, subject="", **env_extra):
    """Run a bash fragment with the subject on stdin and everything else in env.

    Nothing crosses as an argument, for the MSYS re-parsing reason
    `test_grok_helpers.py` sets out: an argv element entering `bash.exe` is
    re-parsed by the MSYS runtime, so the fixture paths below would arrive
    mangled and the failure would be silent.
    """
    env = dict(os.environ)
    # Ambient git configuration is not this suite's subject, and three of the
    # settings it can carry decide what these fixtures reach: `rebase.backend`
    # picks the state directory the helper has to find, `rebase.updateRefs` is
    # one of the two settings it spells a flag against, and `core.autocrlf`
    # rewrites the very files a conflict is staged from. Measured rather than
    # feared: without these, this host's global `core.autocrlf=true` reached
    # every fixture below. `/dev/null` is git's own spelling on all three
    # runners, MSYS included, and is read by git rather than by Python.
    env["GIT_CONFIG_GLOBAL"] = "/dev/null"
    env["GIT_CONFIG_SYSTEM"] = "/dev/null"
    env["GIT_CONFIG_NOSYSTEM"] = "1"
    # The git `setUpModule` probed, not whichever one bash finds first: on a
    # Windows runner those can differ, and the probe would pass against
    # Git-for-Windows while every fixture ran WSL's.
    env["PATH"] = os.pathsep.join([str(Path(GIT).parent), env.get("PATH", "")])
    env.update(env_extra)
    return subprocess.run(
        [BASH, "-c", script],
        input=subject,
        capture_output=True,
        text=True,
        env=env,
    )


# What can introduce a command, beyond the `VAR=value` run: a shell keyword, a
# negation, or one of the wrappers that takes a command as its argument.
# Stripped in a loop, because they stack — `if ! GIT_DIR=x git push …`.
COMMAND_LEAD = re.compile(
    r"^(?:\w+=\S*|then|else|elif|do|done|if|while|until|!|command|exec|time|eval)\s+")

# A command can begin after any of these. `{` and `}` are NOT in the class:
# they open a brace group, but they also spell `"${branch}"`, and splitting
# there cuts a command off before its flags — which is how the first version
# of this scan, written to be wider than the old one, ended up narrower for
# every command using a braced expansion. A brace group's braces are
# space-delimited, so those are matched on their own.
COMMAND_SEPARATOR = re.compile(r"[\n;&|()`]+|(?<=\s)\{(?=\s)|(?<=\s)\}")


def git_commands(source):
    """Every `git …` command in a shell script, however it is introduced.

    A `startswith` scan sees only a command opening a physical line, which
    this script already defeats: `GIT_EDITOR=true git rebase --continue` is
    one of its own, and its dominant failure idiom is a same-line brace group.
    So the text is cut at the separators a command can begin after, and the
    assignments and keywords in front of it are stripped before the word is
    read.

    It does not claim to parse shell. What it claims is to be wider than the
    scan it replaces, in every direction — `test_the_scan_sees_what_the_old_
    one_missed` is where that is established rather than asserted here.
    """
    commands = []
    for piece in COMMAND_SEPARATOR.split(source):
        piece = piece.strip()
        while COMMAND_LEAD.match(piece):
            piece = COMMAND_LEAD.sub("", piece, count=1)
        if piece.startswith("git "):
            commands.append(piece)
    return commands


def code_lines(text):
    """The executable lines of a shell script — comments and blanks dropped.

    Asserted against the whole file, the flag cases below would pass on the
    comments that explain the guards, so deleting a guard would not fail them.
    """
    return [
        line for line in text.splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]

# A remote, a checkout of it, a branch with one commit, and a `main` that has
# moved since — the state every branch update starts from.
FIXTURE = '''
set -eu
root=$(mktemp -d)
git init -q --bare --initial-branch=main "$root/remote.git"
git init -q --initial-branch=main "$root/work"
cd "$root/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$root/remote.git"
echo base > a.txt && git add -A && git commit -qm "the base"
git push -q -u origin main
git checkout -qb feat/x
echo work > b.txt && git add -A && git commit -qm "the branch work"
git push -q -u origin feat/x
git checkout -q main
echo moved > c.txt && git add -A && git commit -qm "main moved"
git push -q origin main
git checkout -q feat/x
printf %s "$root"
'''

# Both sides edit the same line, so the replay cannot proceed without a person.
CONFLICT = '''
git checkout -q main && echo mine > a.txt && git add -A
git commit -qm "main edits a.txt" && git push -q origin main
git checkout -q feat/x && echo theirs > a.txt && git add -A
git commit -qm "the branch edits a.txt" && git push -q origin feat/x
'''


class TheFlagsAreTheScriptsOwn(unittest.TestCase):
    """The grant buys `bash <this file>`, so the flags are never a caller's to
    choose. A prefix rule cannot exclude a trailing flag, which is why this
    repository answers such cases with a helper (`docs/harness-boundaries.md`).
    """

    # The executable lines only. An assertion made against the whole file
    # passes on a comment that mentions the guard, so deleting the guard would
    # not fail it — which is `code_lines`' own argument.
    source = "\n".join(code_lines(HELPER.read_text(encoding="utf-8")))

    def test_there_is_exactly_one_push_and_it_carries_an_expected_value(self):
        pushes = [c for c in git_commands(self.source) if c.startswith("git push")]
        self.assertEqual(
            pushes, ['git push --force-with-lease="$branch:$lease" origin "$branch"'],
            "one push, leased against the commit this run read, naming its remote and its refspec")

    def test_no_spelling_of_the_unleased_force_appears(self):
        # The commands only. Scanning the whole file catches `[ -f "$state/… ]`
        # and every prose mention of the deny this helper exists beside, which
        # is a check that fails on its own documentation.
        #
        # The trailing newline is what lets the `--force\n` spelling match a
        # flag ending the last command.
        # The trailing newline is what lets a flag ENDING a command match, so
        # every spelling needs both forms: `git push origin "$b" -f` was
        # uncovered while `--force` was not.
        commands = "\n".join(git_commands(self.source)) + "\n"
        for spelling in ("--force ", "--force\n", "--force=", " -f ", " -f\n",
                         "--force-if-includes"):
            self.assertNotIn(spelling, commands, f"{spelling!r} would discard without a lease")
        # `+<refspec>` is a force push carrying no flag at all, which this
        # repository's own argv guard already judges as one. Not anchored on a
        # colon or on bare whitespace: `+$branch` forces branch:branch without
        # one, and `"+$branch:$branch"` puts a quote where the space was.
        for command in commands.splitlines():
            if command.startswith("git push"):
                self.assertNotRegex(command, r'(?:^|\s)"?\+\S',
                                    "a `+` refspec forces without naming a flag")

    def test_the_scan_sees_what_the_old_one_missed(self):
        """The scan's subject is the scan, not what it happened to find.

        Every line below is a second, unleased force push. The scan this one
        replaced saw none of the first four, so both flag cases above stayed
        green while the only force-pushing script in the repository grew a
        second one — the failure CLAUDE.md names as the most repeated here.
        """
        hidden = (
            'GIT_SSH_COMMAND=ssh git push --force origin "$branch"',
            'git rev-parse HEAD || { git push --force origin "$branch"; }',
            'if [ -n "$x" ]; then git push --force origin "$branch"; fi',
            'for r in a b; do git push --force origin "$r"; done',
            '! git push --force origin "$branch"',
            'eval git push --force origin "$branch"',
            'git push origin "${branch}" --force',
            'git push origin +$branch',
            'git push origin "+$branch:$branch"',
            'git push origin "$branch" -f',
        )
        baseline = git_commands(self.source)
        for line in hidden:
            with self.subTest(line=line):
                seen = git_commands(self.source + chr(10) + line)
                # Not "exactly one more": a brace-group line is two commands
                # and the scan rightly returns both. What is being asserted
                # is that the FORCE push is among what it now sees, with its
                # flags still attached — a scan that saw the command and cut
                # it off before `--force` would pass a count and fail here.
                self.assertGreater(len(seen), len(baseline),
                                   "the scan does not see this command at all")
                added = seen[len(baseline):]
                self.assertTrue(
                    any("--force" in c or " -f" in c or "+" in c for c in added),
                    f"the scan truncated the command before its flags: {added!r}")

    def test_the_push_leases_against_the_value_the_guard_approved(self):
        # Re-reading the remote ref in `publish` would lease against whatever a
        # fetch had since made of it, with the whole replay in between — the
        # lease would then name the commits the guard refused.
        self.assertIn('lease="$approved_lease"', self.source)
        # A pattern, not a literal. Counting one exact spelling left
        # `approved_lease=$(git rev-parse refs/remotes/origin/"$branch")` as a
        # second read the assertion could not see, and the negative below it
        # was anchored on `lease=`, which `^` keeps from ever matching
        # `approved_lease=` — so between them they forbade nothing.
        reads = re.findall(r"\w*lease=\$\(\s*git rev-parse", self.source)
        self.assertEqual(1, len(reads),
                         "one place reads the remote tip into a lease, and it is the guard")
        # And `publish` re-reads no REMOTE ref, which is the property — it
        # runs after the replay, so a lease built there names whatever a
        # fetch has since made of the tip. Narrowed to `refs/remotes`
        # deliberately: `head=$(git rev-parse HEAD)` lives in that function
        # too and is not a lease, so forbidding `rev-parse` outright fails on
        # the function doing its job.
        body = self.source.split("publish() {", 1)[1].split(chr(10) + "}", 1)[0]
        self.assertNotRegex(body, r'rev-parse\s+"?refs/remotes',
                            "publish re-reading a remote ref would lease against "
                            "whatever a fetch has since made of it")


class TheHelperRefusesBeforeItRewrites(unittest.TestCase):

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def helper(self, branch, mode="start"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def head(self):
        return self.at("git rev-parse HEAD").stdout.strip()

    def test_it_takes_a_branch_and_a_mode(self):
        one = run_bash('cd "$W" && bash "$H" feat/x', W=self.work, H=str(HELPER))
        self.assertEqual(2, one.returncode)
        self.assertEqual(2, self.helper("feat/x", "land").returncode, "an unknown mode is refused")

    def test_main_is_refused_even_while_main_is_checked_out(self):
        self.at("git checkout -q main")
        self.assertEqual(3, self.helper("main").returncode)

    def test_main_is_refused_however_it_is_spelled(self):
        # One string compare is one spelling, and this host's filesystem is
        # case-insensitive: `git branch Main` answers that it already exists.
        for spelling in ("main", "Main", "MAIN", "heads/main", "refs/heads/main",
                         "origin/main", "refs/remotes/origin/main"):
            result = self.helper(spelling)
            self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")

    def test_naming_another_branch_does_not_act_on_it(self):
        # The guard is the refusal, not the exit code: `publish` exits 4 as
        # well, and would have rebased the other branch on the way there.
        self.at("git checkout -qb feat/other")
        other_before = self.at("git rev-parse feat/other").stdout.strip()
        result = self.helper("feat/x")
        self.assertEqual(4, result.returncode)
        self.assertIn("only ever touches the current branch", result.stderr)
        self.assertEqual(other_before, self.at("git rev-parse feat/other").stdout.strip(),
                         "the branch in hand was replayed on the way to the refusal")

    def test_a_detached_head_is_refused(self):
        self.at("git checkout -q --detach HEAD")
        result = self.helper("feat/x")
        self.assertEqual(4, result.returncode)
        self.assertIn("detached HEAD", result.stderr,
                      "the equality check below exits 4 too, so the code alone proves nothing")

    def test_a_dirty_tree_is_refused(self):
        self.at("echo uncommitted >> b.txt")
        self.assertEqual(5, self.helper("feat/x").returncode)
        self.assertIn("uncommitted", self.at("cat b.txt").stdout, "the edit survives the refusal")

    def test_a_branch_the_remote_does_not_have_is_refused(self):
        self.at("git checkout -qb feat/unpublished")
        self.assertEqual(6, self.helper("feat/unpublished").returncode)

    def test_commits_only_the_remote_has_stop_it(self):
        # A lease is satisfied by a commit this checkout has fetched, so it
        # would not stop a push that knowingly discards another session's work.
        # This is the guard that does.
        self.at('git clone -q ../remote.git ../second')
        run_bash('cd "$R/second" && git config user.email t@e.invalid && git config user.name T '
                 '&& git checkout -q feat/x && echo theirs > theirs.txt && git add -A '
                 '&& git commit -qm "another session" && git push -q origin feat/x', R=self.root)
        before = self.head()
        result = self.helper("feat/x")
        self.assertEqual(7, result.returncode)
        # Exit 7 is reached from three places, so the code alone does not say
        # this guard fired: moving the call after the replay would reach "no
        # lease was approved" with the branch already rewritten, and this case
        # would still pass. The message and the untouched tip are what pin it.
        self.assertIn("carries commits this checkout did not start from", result.stderr)
        self.assertEqual(before, self.head(), "the branch was rewritten before the refusal")

    def test_continue_and_abort_refuse_when_no_rebase_is_running(self):
        # The code alone proves nothing: exit 9 is shared by refusals across
        # all four modes — the script owns the count — and each of these has
        # same-code neighbours inside its own mode that it must not be
        # passing on.
        finish = self.helper("feat/x", "continue")
        self.assertEqual(9, finish.returncode, finish.stderr)
        self.assertIn("'continue' has nothing to finish", finish.stderr)
        undo = self.helper("feat/x", "abort")
        self.assertEqual(9, undo.returncode, undo.stderr)
        self.assertIn("'abort' has nothing to undo", undo.stderr)

    def test_a_rebase_this_helper_did_not_start_is_not_published(self):
        # An interactive rebase dropping the branch's commits passes every
        # other check in `continue`: the published tip is what it started
        # from, so the divergence guard is satisfied and the result — which
        # holds none of the branch's work — would be forced over it.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        # `-i` with an attached suffix, which GNU and BSD sed both take; bare
        # `-i` is GNU-only and CI's harness job runs macos-latest, where the
        # rebase would never start and this case would assert on the wrong
        # refusal. The backup lands inside the rebase state and goes with it.
        self.at('GIT_SEQUENCE_EDITOR="sed -i.bak 1s/^pick/break/" '
                'git rebase -i refs/remotes/origin/main')
        result = self.helper("feat/x", "continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not started by this helper", result.stderr)
        self.assertEqual(published, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote still carries the branch's published work")
        self.at("git rebase --abort")

    def test_a_rebase_this_helper_did_not_start_is_not_aborted(self):
        # `continue`'s identical check is covered above and this twin was
        # not, though it is the more destructive of the two: `continue` on a
        # foreign rebase would publish, where `abort` runs `git rebase
        # --abort` and throws away a replay somebody is standing in. Removing
        # the check left every other case in this file green.
        self.at('GIT_SEQUENCE_EDITOR="sed -i.bak 1s/^pick/break/" '
                'git rebase -i refs/remotes/origin/main')
        result = self.helper("feat/x", "abort")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not this helper's to undo", result.stderr)
        self.assertEqual(
            "yes",
            self.at('test -d "$(git rev-parse --git-path rebase-merge)" '
                    '-o -d "$(git rev-parse --git-path rebase-apply)" '
                    '&& echo yes || echo no').stdout.strip(),
            "the foreign rebase was thrown away")
        self.at("git rebase --abort")

    def test_publish_refuses_when_no_replay_is_waiting(self):
        # `publish` is the retry path, and with nothing to retry it must not
        # fall through to a push. Exit 9 again, so the message is the guard.
        result = self.helper("feat/x", "publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("no replay is waiting to be published", result.stderr)

    def test_the_second_site_of_exit_six_has_its_own_case(self):
        # Exit 6 is two guards — no `refs/remotes/origin/main` to rebase onto,
        # and `origin has no <branch>` — and only the second had a case. A
        # branch that was simply never pushed cannot reach the first, because
        # `start` fetches before it looks, so the remote has to lose `main`
        # and the fetch has to prune it.
        run_bash('cd "$R/remote.git" && git symbolic-ref HEAD refs/heads/feat/x '
                 '&& git update-ref -d refs/heads/main', R=self.root)
        self.at("git config remote.origin.prune true")
        result = self.helper("feat/x")
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("no refs/remotes/origin/main", result.stderr)

    def test_a_rebase_that_never_started_is_not_called_a_conflict(self):
        # Every other way `git rebase` can fail leaves no state, and reporting
        # it as a conflict sends the caller to a `continue` with nothing to do.
        self.at('printf "#!/bin/sh\\nexit 1\\n" > "$(git rev-parse --git-path hooks)/pre-rebase" '
                '&& chmod +x "$(git rev-parse --git-path hooks)/pre-rebase"')
        result = self.helper("feat/x")
        self.assertEqual(11, result.returncode, result.stderr)
        self.assertIn("did not start", result.stderr)


class AConflictIsTheCaseRebaseIsHereFor(unittest.TestCase):
    """The resolution belongs in the replayed commit, not in a merge commit.

    So `start` leaves a conflicted rebase in progress rather than aborting it:
    backing out would send the caller to the merge this repository stopped
    making, and the tree would stop being a line.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode, branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def rebase_running(self):
        return self.at('test -d "$(git rev-parse --git-path rebase-merge)" '
                       '-o -d "$(git rev-parse --git-path rebase-apply)" '
                       '&& echo yes || echo no').stdout.strip()

    def test_a_conflict_leaves_the_rebase_in_progress_and_names_the_paths(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode)
        # Joined to the helper's own banner, not asserted loose. The fixture's
        # conflicting commit is titled "the branch edits a.txt", so git puts
        # that path on stderr before the helper prints anything — a bare
        # `assertIn("a.txt", …)` passed with the path list deleted from the
        # script, in the case whose name is `..._and_names_the_paths`.
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "the caller is told which files to resolve")
        self.assertEqual("yes", self.rebase_running(), "aborting here would mean a merge instead")

    def test_continue_refuses_while_anything_is_still_unmerged(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.assertEqual(9, self.helper("continue").returncode, "nothing was resolved")
        self.assertEqual("yes", self.rebase_running())

    def test_resolving_and_continuing_publishes_a_line(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual("resolved\n", self.at("cat a.txt").stdout,
                         "the resolution is in the replayed commit")
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "and no merge commit was made — the whole point")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote carries what was replayed")

    def test_continue_fails_closed_when_the_starting_commit_is_unreadable(self):
        # The lease alone would not stop this: another session's commits that
        # this checkout has fetched satisfy it. Skipping the check that does
        # would leave the only force push in the repository unguarded.
        self.assertEqual(8, self.helper("start").returncode)
        self.at('rm -f "$(git rev-parse --git-path rebase-merge)"/orig-head '
                '"$(git rev-parse --git-path rebase-merge)"/head')
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("divergence", result.stderr)

    def test_start_refuses_while_a_rebase_is_already_in_progress(self):
        # Exit 9's `start` arm, and not the waiting-record refusal just below
        # it in the same mode: that one is reached with no rebase running.
        self.assertEqual(8, self.helper("start").returncode)
        again = self.helper("start")
        self.assertEqual(9, again.returncode, again.stderr)
        self.assertIn("a rebase is already in progress", again.stderr)
        self.assertEqual("yes", self.rebase_running(), "the running rebase was disturbed")

    def test_continue_refuses_a_rebase_belonging_to_another_branch(self):
        # The name passed is not the branch the rebase will restore, so
        # finishing it would force the result over a branch this run never
        # replayed.
        self.assertEqual(8, self.helper("start").returncode)
        result = self.helper("continue", branch="feat/other")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the rebase in progress is feat/x, not feat/other", result.stderr)
        self.assertEqual("yes", self.rebase_running())

    def test_publish_refuses_while_a_rebase_is_still_in_progress(self):
        self.assertEqual(8, self.helper("start").returncode)
        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("a rebase is still in progress", result.stderr)
        self.assertEqual("yes", self.rebase_running())

    def test_abort_puts_the_branch_back_and_publishes_nothing(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        remote_before = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.assertEqual(0, self.helper("abort").returncode)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(remote_before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())


class ALegacyMergeForwardIsNotSilentlyDropped(unittest.TestCase):
    """A rebase drops merge commits, and a branch made under the old policy
    has one. Where that merge carries content neither parent has, replaying
    loses it before the push, which no lease can see."""

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def merge_forward(self):
        self.at('git checkout -q main && echo later > d.txt && git add -A '
                '&& git commit -qm "main moved again" && git push -q origin main '
                '&& git checkout -q feat/x && git merge --no-edit -q main')

    def test_a_merge_carrying_its_own_content_stops_the_run(self):
        self.merge_forward()
        self.at('echo only-here > resolved-by-hand.txt && git add -A '
                '&& git commit -q --amend --no-edit && git push -q -f origin feat/x')
        before = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()
        self.assertEqual(10, result.returncode, result.stderr)
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip(), "nothing was replayed")
        self.assertEqual("only-here\n", self.at("cat resolved-by-hand.txt").stdout)

    def test_an_ordinary_merge_forward_is_flattened_without_complaint(self):
        # Its content is in its parents, so dropping it loses nothing.
        self.merge_forward()
        self.at('git push -q -f origin feat/x')
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "the merge is gone and the branch is a line")


class TheHelperPublishesWhatItRebased(unittest.TestCase):

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def test_a_clean_update_is_replayed_and_published(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)

        after = self.at("git rev-parse HEAD").stdout.strip()
        self.assertNotEqual(before, after, "a replay onto a moved base makes new SHAs")
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "a rebased branch carries no merge commit")
        self.assertEqual(after, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "and the branch now holds everything main does")

    def test_configuration_cannot_change_what_the_replay_does(self):
        # Both settings are the caller's, and both break a stated guarantee:
        # one keeps the merge commits this helper exists to be rid of, the
        # other force-updates other local branches' refs as a side effect.
        self.at("git config rebase.rebaseMerges true && git config rebase.updateRefs true")
        self.at('git checkout -q main && echo later > d.txt && git add -A '
                '&& git commit -qm "main moved again" && git push -q origin main '
                '&& git checkout -q feat/x && git merge --no-edit -q main '
                '&& git push -q -f origin feat/x')
        self.assertEqual(0, self.helper().returncode)
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "rebase.rebaseMerges would have kept the merge")

    def test_a_branch_pointing_inside_the_replayed_range_is_not_moved(self):
        # The case above names `rebase.updateRefs` and creates no second ref
        # inside the replayed range, so the setting had nothing to move and
        # `--no-update-refs` was asserted by nothing: dropping the flag failed
        # nothing, while a caller carrying that setting got other local
        # branches force-moved by a helper documented as touching only the
        # current branch.
        self.at("git branch marker")
        self.at('echo more > e.txt && git add -A && git commit -qm "a second commit" '
                '&& git push -q -f origin feat/x')
        self.at("git config rebase.updateRefs true")
        before = self.at("git rev-parse marker").stdout.strip()
        self.assertTrue(before, "the marker branch was not created")

        self.assertEqual(0, self.helper().returncode)
        self.assertEqual(before, self.at("git rev-parse marker").stdout.strip(),
                         "rebase.updateRefs force-moved a branch this helper does not own")

    def test_a_push_that_fails_leaves_a_retry_that_works(self):
        # The replay finishes and the push does not, taking the rebase state
        # with it: `start` then reads the rewritten tip as non-ancestral and
        # `continue` finds no rebase, so without the record nothing can reach
        # the branch again.
        # A `pre-push` hook, because the fetch has to succeed for the replay to
        # happen at all — a broken remote fails earlier and proves nothing.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail for this to mean anything")
        rewritten = self.at("git rev-parse HEAD").stdout.strip()
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "the replay did finish; only the push did not")

        self.assertEqual(9, self.helper("start").returncode, "start refuses while a replay waits")
        self.at(hooks + '; rm -f "$h/pre-push"')
        result = self.helper("publish")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(rewritten, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())

    def test_publish_refuses_a_record_whose_replay_never_ran(self):
        # A two-field record is a run that stopped before a replayed commit
        # existed, and without the head `publish` would force whatever HEAD
        # has since become. It is reached when a conflicted rebase is undone
        # by hand rather than through this helper: git takes the rebase state
        # with it and knows nothing about the record.
        self.at(CONFLICT)
        self.assertEqual(8, self.helper().returncode, "the start must conflict")
        self.at("git rebase --abort")
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.at('echo unrelated > later.txt && git add -A && git commit -qm "not a replay"')

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        # The message, not the code: exit 9 is shared by refusals throughout
        # the script, and the "no replay is waiting" one above this guard is
        # a state this case must not be passing on.
        self.assertIn("never finished", result.stderr)
        self.assertEqual(published,
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote still carries what it had")

    def test_a_rebase_that_never_started_leaves_no_record(self):
        # Nothing was replayed, so there is no rewritten tip for a record to
        # protect — and one left behind refuses every later `start`, on any
        # branch, until somebody aborts from the branch it names.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-rebase"; chmod +x "$h/pre-rebase"')
        self.assertEqual(11, self.helper().returncode, "the rebase must not start")
        self.assertEqual(
            "", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
            "a record was left for a replay that never happened")

        # The positive control, and the reason this matters: the next run is
        # not refused by the leftover.
        self.at(hooks + '; rm -f "$h/pre-rebase"')
        self.assertEqual(0, self.helper().returncode, "a stale record refused the next start")

    def test_abort_clears_a_record_whose_remote_ref_is_gone(self):
        # A missing remote-tracking ref is a genuinely dead lease — `publish`
        # refuses at `require_remote_branch` — so clearing is right, and this
        # is the path the rewritten read has to keep working. What must NOT
        # reach it is a `git rev-parse` failing for any other reason, which
        # would strand the replay; that is why absence is now asked as its own
        # question rather than inferred from a failure.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        self.at("git update-ref -d refs/remotes/origin/feat/x")

        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared the waiting replay", result.stdout)

    def test_abort_refuses_a_replay_that_publish_can_still_finish(self):
        # Clearing a record whose head is still the tip leaves the branch
        # rewritten with nothing able to reach it: `start` reads that tip as
        # non-ancestral, `continue` finds no rebase, and an ordinary push is
        # not a fast-forward. `abort` offering that is how the branch is lost.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        rewritten = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper("abort")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("refusing to strand it", result.stderr)

        # The positive control: the record survived the refusal, so the
        # replay is still publishable.
        self.at(hooks + '; rm -f "$h/pre-push"')
        self.assertEqual(0, self.helper("publish").returncode)
        self.assertEqual(rewritten,
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "publish finished the replay the abort refused to discard")

    def test_publish_refuses_a_head_that_is_not_the_one_replayed(self):
        # The record survives a push that failed after the replay, and the
        # retry republishes THAT commit. A tip amended or added to in between
        # is a different branch, and forcing it is what the record refuses.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.at('echo extra > extra.txt && git add -A && git commit -qm "after the replay"')
        self.at(hooks + '; rm -f "$h/pre-push"')

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not the commit this helper replayed", result.stderr)
        self.assertEqual(published,
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the commit made after the replay was not forced over it")

    def test_abort_refuses_a_waiting_replay_belonging_to_another_branch(self):
        # Two ways to reach the wrong record - the name passed and the branch
        # in hand - and clearing it strands a tip nothing else can publish.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        self.at("git checkout -qb feat/other")

        named = self.helper("abort", branch="feat/other")
        self.assertEqual(4, named.returncode, named.stderr)
        # The whole sentence: both exit-4 sites in this block interpolate a
        # branch name, so `feat/x` alone would pass on the other one — and
        # reordering the two checks would leave this green while the guard it
        # was written for was no longer the one reached.
        self.assertIn("the waiting replay is feat/x, not feat/other", named.stderr)

        elsewhere = self.helper("abort", branch="feat/x")
        self.assertEqual(4, elsewhere.returncode, elsewhere.stderr)
        self.assertIn("only ever touches the current branch", elsewhere.stderr)

        # The positive control: the record survived both refusals, so the
        # replayed tip is still reachable.
        self.at("git checkout -q feat/x")
        self.at(hooks + '; rm -f "$h/pre-push"')
        self.assertEqual(0, self.helper("publish").returncode)

    def test_publish_refuses_a_record_naming_another_branch(self):
        # The record is one per checkout, so the name passed is a way to
        # reach somebody else's replay.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")

        result = self.helper("publish", branch="feat/other")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the waiting replay is feat/x, not feat/other", result.stderr)

    def test_publish_refuses_a_remote_that_moved_since_the_lease(self):
        # The lease was approved against a tip that no longer exists, and
        # re-approving it here is the re-read this helper exists to avoid —
        # the force would then name the very commits the guard refused.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        self.at("git update-ref refs/remotes/origin/feat/x refs/remotes/origin/main")

        result = self.helper("publish")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has moved since the replay was approved", result.stderr)

    def test_publish_refuses_a_replay_that_ended_on_no_branch(self):
        # `publish()`'s own current-branch check, reached past every guard in
        # the mode above it: the tip is the replayed commit and the remote is
        # where the lease left it, so only this one is left to refuse.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        self.at("git checkout -q --detach HEAD")

        result = self.helper("publish")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the replay ended on a detached HEAD", result.stderr)

        # `publish()`'s other refusal — an empty `approved_lease` — has no
        # case and cannot be given one from a granted mode. `start` and
        # `continue` both set it in `require_remote_carries_nothing_new`
        # before the replay, and `publish` sets it from the record, which
        # `read_pending` splits on whitespace: a record carrying a head but
        # no lease cannot be written. It is a belt-and-braces assertion, and
        # recording that here is honester than a case that fakes reaching it.

    def test_an_unreadable_waiting_record_is_refused(self):
        # A truncated record used to kill the run bare under `set -e`, which
        # is a code this script assigns to nothing, in the path that exists
        # to recover from a failed run.
        hooks = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
        self.at(hooks + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"')
        self.assertNotEqual(0, self.helper().returncode, "the push must fail")
        self.at('printf "\\n" > "$(git rev-parse --git-path claude-rebase-pending)"')

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("the waiting record is unreadable", result.stderr)
        # Named for the guard it reaches. `read` itself succeeds on an empty
        # line — it is a complete line — so what refuses here is the
        # emptiness check, not the read. A truncated record is a different
        # path: `read` fails with the branch already assigned, and `publish`
        # refuses it at "names no replayed commit", which
        # `test_publish_refuses_a_record_whose_replay_never_ran` covers.

    def test_a_second_run_changes_nothing_and_does_not_force(self):
        self.assertEqual(0, self.helper().returncode)
        settled = self.at("git rev-parse HEAD").stdout.strip()
        again = self.helper()
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertIn("nothing to force", again.stdout)
        self.assertEqual(settled, self.at("git rev-parse HEAD").stdout.strip())


class TheBranchNameGuardsEachHaveTheirOwnCase(unittest.TestCase):
    """Exit 2 covers five conditions and three of them had no case at all.

    Asserting the code alone is what let that stand: an unknown mode already
    returns 2, so weakening any of the three name guards failed nothing while
    `--onto` or `-D` as a branch name reached `git push --force-with-lease`.
    Each case here pins the message too, because the message is the only
    thing that says which of the five fired.

    No fixture, and that is the property rather than a convenience: these
    refuse before anything reads the checkout, so a guard that needed a
    repository would be one the session's location could switch off.
    """

    def refuse(self, branch, mode="start"):
        return run_bash('bash "$H" "$B" "$M"', H=str(HELPER), B=branch, M=mode)

    def test_a_leading_dash_is_refused_before_it_can_be_a_flag(self):
        for branch in ("-D", "--onto", "-f"):
            with self.subTest(branch=branch):
                result = self.refuse(branch)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("may not start with '-'", result.stderr)

    def test_a_range_is_refused(self):
        result = self.refuse("feat/a..feat/b")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("may not contain '..'", result.stderr)

    def test_anything_the_pattern_does_not_admit_is_refused(self):
        # The widest of the three: the two above name one shape each, and
        # this is what keeps the rest from reaching a push.
        for branch in ("feat/x;id", "feat/x y", ".hidden", "feat/x$(id)", ""):
            with self.subTest(branch=branch):
                result = self.refuse(branch)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("not a branch name this helper will take", result.stderr)

    def test_an_unknown_mode_is_refused_by_its_own_message(self):
        result = self.refuse("feat/x", "skip")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("mode must be start, continue, publish or abort", result.stderr)

    def test_it_takes_exactly_two_arguments(self):
        # The fifth exit-2 condition, and the one the suite asserted by code
        # alone. Relaxing `[ "$#" -eq 2 ]` to `-ge 2` left every other case
        # green while the helper silently ignored trailing arguments — which
        # is the "a prefix rule cannot exclude a trailing flag" case a helper
        # exists to answer in the first place.
        for args in ("", '"$B"', '"$B" "$M" --onto'):
            with self.subTest(args=args or "(none)"):
                result = run_bash('bash "$H" ' + args,
                                  H=str(HELPER), B="feat/x", M="start")
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("usage: git-rebase-onto-main.sh", result.stderr)


class TheApplyBackendIsDrivenToo(unittest.TestCase):
    """`rebase.backend=apply` is the caller's setting and this helper supports
    it by construction — it looks for `rebase-apply` beside `rebase-merge`.

    Every other case here runs the merge backend, so deleting the
    `rebase-apply` arms failed nothing, while a caller carrying that setting
    was told "no rebase is in progress" for one that is. It is also the only
    backend that can reach the empty-replay path: the merge backend drops
    such a commit itself and says nothing, which is why the wedge #38 filed
    was invisible to a suite that never left it.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.at("git config rebase.backend apply")
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def test_the_state_this_backend_leaves_is_the_one_the_helper_finds(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "git names the path too, so this joins it to the helper's banner")
        self.assertEqual("yes", self.at('test -d "$(git rev-parse --git-path rebase-apply)" '
                                        '&& echo yes || echo no').stdout.strip(),
                         "this case is not driving the apply backend at all")
        self.at("echo resolved > a.txt && git add a.txt")
        self.assertEqual(0, self.helper("continue").returncode)

    def test_an_empty_resolution_is_dropped_rather_than_called_a_conflict(self):
        # #38's one finding with teeth. Taking origin/main's side verbatim is
        # the commonest merge-forward resolution and leaves nothing to commit,
        # which this backend refuses. Every such failure used to be reported
        # as a fresh conflict — "resolve these", naming nothing, exit 8 — and
        # the escape git asks for is `--skip`, which nothing here granted.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("empty", result.stderr, "the caller is told the commit was dropped")
        self.assertEqual("no", self.at('test -d "$(git rev-parse --git-path rebase-apply)" '
                                       '&& echo yes || echo no').stdout.strip(),
                         "the replay is still wedged in progress")
        self.assertEqual("mine\n", self.at("cat a.txt").stdout,
                         "the resolution taken is the one on the branch")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote carries what was replayed")

    def test_a_conflict_after_the_drop_is_reported_rather_than_skipped_too(self):
        # The loop's second pass. Dropping an empty commit puts the next one
        # into the replay, and if that conflicts the run is back at the first
        # of the three states — so the pass that follows a skip has to answer
        # exactly as the first one did, rather than skipping again.
        # A second commit touching the same file, so that dropping the first
        # as empty puts a conflicting one into the replay rather than ending
        # it. b.txt would not do: main does not carry it, so a commit adding
        # it there would collide on the branch's first commit instead.
        self.at('echo theirs-again > a.txt && git add -A '
                '&& git commit -qm "the branch edits a.txt again" '
                '&& git push -q -f origin feat/x')
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")

        result = self.helper("continue")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("empty", result.stderr, "the first commit was not dropped")
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "the conflict after the drop was skipped instead of reported")
        self.assertEqual("yes", self.at('test -d "$(git rev-parse --git-path rebase-apply)" '
                                        '&& echo yes || echo no').stdout.strip(),
                         "the replay was not left in progress for the caller")

    def test_the_empty_commit_is_the_only_thing_the_drop_loses(self):
        # The positive control for the case above: what a skip discards is a
        # commit with nothing in it, and the branch's other work survives.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        self.assertEqual(0, self.helper("continue").returncode)
        self.assertEqual("work\n", self.at("cat b.txt").stdout,
                         "the branch's own commit was dropped along with the empty one")
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "the branch does not hold everything main does")


class AStoppedReplayIsNotAlwaysAConflict(unittest.TestCase):
    """The third state, and the one no message can be guessed for.

    A replay can stop with its state intact, nothing unmerged, and a real
    change staged — a commit git would not make for a reason of its own. It
    is neither a conflict nor an empty replay, so it is reported as itself
    rather than skipped: dropping a commit that holds content is the one
    outcome the empty-replay path must never reach.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def test_a_replay_that_stopped_for_another_reason_is_reported_as_itself(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo different > a.txt && git add a.txt")
        # An identity git will not commit under: empty rather than unset, so
        # it holds whatever the runner's own configuration is.
        self.at('git config user.name "" && git config user.email ""')

        result = self.helper("continue")
        self.assertEqual(14, result.returncode, result.stderr)
        # The staged half of exit 14, not merely the code and not merely the
        # word "empty": the unstaged message carries that too, and this case
        # must not pass on it.
        self.assertIn("nothing unmerged and a change still staged", result.stderr)
        self.assertNotIn("resolve these", result.stderr,
                         "the caller is sent to resolve files that are named nowhere")
        self.assertEqual("yes", self.at('test -d "$(git rev-parse --git-path rebase-merge)" '
                                        '-o -d "$(git rev-parse --git-path rebase-apply)" '
                                        '&& echo yes || echo no').stdout.strip(),
                         "the replay was thrown away rather than left to be answered")
        self.assertEqual("different\n", self.at("git show :a.txt").stdout,
                         "the staged change was dropped by a skip that must not have run")

    def test_the_merge_backend_drops_an_empty_replay_on_its_own(self):
        # Why the apply-backend case above is where the wedge lives. This
        # backend never asks, so `continue` here never reaches `stopped`.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("mine\n", self.at("cat a.txt").stdout)
        # Without this the case held whichever way the commit was dropped —
        # by git, or by the helper's own skip path, which is what the sibling
        # class establishes for the other backend. The property named in the
        # comment above is that the skip path did NOT run.
        self.assertNotIn("empty", result.stderr,
                         "the helper's skip path ran; this backend drops it itself")

    def test_unstaged_work_is_not_discarded_by_the_drop(self):
        # Round 1 of #38 lost this, and it is the reason the empty-replay
        # path takes two reads. `git rebase --continue` refuses when a
        # tracked file carries an unstaged edit, and that leaves the index
        # equal to HEAD — so an emptiness test asked of the index alone
        # passes, and `git rebase --skip` hard-resets the edit away.
        #
        # Measured against the one-read version: b.txt came back holding only
        # "work", and the branch was force-pushed without the rest.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        self.at("echo precious >> b.txt")
        before = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertTrue(before, "the remote tip was not read")

        result = self.helper("continue")
        self.assertEqual(14, result.returncode, result.stderr)
        self.assertIn("unstaged changes in the tree", result.stderr)
        self.assertIn("b.txt", result.stderr, "the caller is told what is in the way")
        self.assertEqual("work\nprecious\n", self.at("cat b.txt").stdout,
                         "the unstaged edit was hard-reset away by the skip")
        self.assertEqual(before,
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the branch was published without the work still in the tree")


class EveryFixtureSaysItWasBuilt(unittest.TestCase):
    """The suite's own gate, and this file's history is why it exists.

    Three of the classes building FIXTURE had lost the
    `assertTrue(self.root)` guard by copying the fixture rather than sharing
    it. With a failed fixture `self.root` is "" and `self.work` is "/work", so
    every `cd` fails and the `assertNotEqual(0, …)` and empty-stdout
    assertions are satisfied by the failed `cd` rather than by the state they
    name — a class of cases that cannot fail, which is the shape CLAUDE.md
    calls this repository's most repeated failure.

    A shared base class is the other fix and is not taken: the `helper()`
    signatures differ in argument order between these classes, so unifying
    them is a rewrite of every call site rather than a guard. This asserts
    the property instead, and asserts it about what it is looking at.

    **Read structurally, not as text.** A substring scan of the `setUp`
    source took a commented-out `# self.assertTrue(self.root, …)` for the
    real thing, and would have gone red on a `setUp` that called the
    assertion through a helper — a gate wrong in both directions at once.
    The AST answers both: a comment is not in it, and a call is a call.
    """

    @classmethod
    def builds_the_fixture(cls, function):
        # Any mention of the name at all, anywhere in the `setUp`. Matching
        # the call's shape instead — `run_bash(FIXTURE)` with FIXTURE first
        # and positional — missed `run_bash(FIXTURE + CONFLICT)`,
        # `run_bash(script=FIXTURE)` and a build moved into a helper, and a
        # class it fails to recognise is a class it never asks about.
        return any(isinstance(node, ast.Name) and node.id == "FIXTURE"
                   for node in ast.walk(function))

    @classmethod
    def asserts_its_root(cls, function):
        # The statement's own call, read directly. Restricting to top-level
        # statements and then walking each one's subtree closed the `if`
        # route and left the lambda one open:
        # `self.addCleanup(lambda: self.assertTrue(self.root, ...))` is a
        # top-level expression statement whose assertion never runs, and that
        # `addCleanup(lambda: ...)` idiom is in every `setUp` in this file, so
        # it is a plausible mis-edit rather than a contrived one. Two rounds
        # of this gate have now claimed to read a call as a call while
        # reading a subtree; this one reads the call.
        #
        # An assertion wrapped in `with` or `try` reads as absent and reddens
        # the gate. That is the direction to fail in: somebody looks, rather
        # than the gate approving a `setUp` it did not understand.
        for statement in function.body:
            if not isinstance(statement, ast.Expr):
                continue
            call = statement.value
            if not (isinstance(call, ast.Call)
                    and isinstance(call.func, ast.Attribute)
                    and call.func.attr == "assertTrue"
                    and isinstance(call.func.value, ast.Name)
                    and call.func.value.id == "self"):
                continue
            subject = call.args[0] if call.args else None
            if (isinstance(subject, ast.Attribute)
                    and subject.attr == "root"
                    and isinstance(subject.value, ast.Name)
                    and subject.value.id == "self"):
                return True
        return False

    def test_every_class_that_builds_the_fixture_says_it_was_built(self):
        watched = []
        for node in ast.parse(Path(__file__).read_text(encoding="utf-8")).body:
            if not isinstance(node, ast.ClassDef):
                continue
            for member in node.body:
                if not isinstance(member, ast.FunctionDef) or member.name != "setUp":
                    continue
                if not self.builds_the_fixture(member):
                    continue
                watched.append(node.name)
                self.assertTrue(
                    self.asserts_its_root(member),
                    f"{node.name} builds the fixture and never says whether it "
                    "worked: a failed one makes self.work '/work' and every "
                    "assertion under it vacuous")
        # The gate's own subject, and exact rather than a floor — but what
        # that buys is narrow, and the first version of this comment credited
        # it with more. A seventh class this gate DOES recognise can no
        # longer arrive unremarked, because the count has to be bumped in the
        # same edit, which is the moment to ask whether the new one asserts
        # its root. A seventh class the recogniser MISSES leaves the count at
        # six and passes here exactly as a floor would; matching the bare
        # name anywhere in the `setUp` is what narrows that hole, and nothing
        # in this file closes it.
        self.assertEqual(6, len(watched),
                         f"this gate is watching {watched}: a fixture class was "
                         "added or renamed. Check that each one asserts its root, "
                         "then bump this number deliberately")


if __name__ == "__main__":
    unittest.main()
