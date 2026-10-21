#!/usr/bin/env python3
"""Judge git on the argv the shell will execute, not on the string a caller types.

**This exists because a permission rule matches the typed STRING and the shell
executes an ARGV**, and the gap between those two is where #30 lived.
`.claude/settings.json` denies `Bash(git *--output*)`, which closes the naive
spelling and nothing more: the shell reassembles adjacent quoted fragments
before `exec`, so `--out''put=<path>` reaches git as `--output=<path>` while
never presenting the matcher with a contiguous `--output`. Measured, not
reasoned about — `printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`.
`docs/harness-boundaries.md` records that as an accepted limit and names the
fix: "a helper that spells its own flags, or a rule over the executed argv
rather than the typed string". This is the second. It was `CLAUDE.md`'s
paragraph until the extraction; the pointer moved with the argument, because
this comment names the file to rewrite when the bound changes rather than
merely citing one.

It also closes one thing the rule system provably cannot express. `Bash(git
*ext::*)` passes settings validation and then matches NOTHING — the trailing
`:*` is consumed as the prefix-wildcard form — while `Bash(git *ext::**)` is
rejected at startup. So `ext::`, a git transport that RUNS its argument as a
command, has no expressible Bash deny. It has one here.

**The push half is an ALLOW-list, and that is the whole design (#23).** It began
as a deny-list of dangerous spellings and two review rounds took it apart, each
finding a form nobody had listed: `--force-with-lease=<ref>` (not equal to the
set entry), `--for` (git accepts unambiguous abbreviations), `-fv` (bundled
shorts), `--all` and `--branches` and `--mirror` and `--prune` (no refspec to
inspect), `refs/heads/*:refs/heads/*` (a wildcard destination that includes
`main` and equals nothing), `git push origin HEAD` and bare `git push origin`
(no destination named at all, so nothing can be shown NOT to be `main`).

That is the deny-list trailing the grammar — the exact failure #23 is about,
reappearing inside its fix in parser form. **So the question is inverted:** a
push is refused unless every part of it is recognised. One remote, one refspec
that names a destination, and options drawn from a fixed set. Everything else,
including every spelling nobody has thought of yet, is refused. The three
pushes `/ship` actually makes are pinned in the suite, so over-reach breaks
there rather than in the delivery chain.

**Three things the parser has to do before it can judge anything**, each found
by a reviewer after the previous fix looked complete:

  * **heredoc bodies are data.** `shlex` knows nothing about them, so a commit
    body was tokenised as arguments — refusing an honest commit that quoted a
    push, and only passing the first test because an apostrophe forced the
    fallback path. Stripped first.
  * **operators without spaces still separate commands.** `shlex.split` left
    `--oneline&&git` as one element, so `git log --oneline&&git push origin
    +HEAD:main` never started a second segment and the push was admitted.
    `punctuation_chars=True` fixes it and leaves quoted content alone.
  * **a command substitution is executed, not quoted away.** `git log "$(git
    push origin +HEAD:main)"` is one `shlex` token and two commands to the
    shell. Substitutions are extracted and judged in their own right.

**What a value-taking flag is depends on the SUBCOMMAND**, and defaulting the
other way was a hole: `-m` takes a value for `commit` and takes none for `log`,
so a global skip-list let `git log -m --out''put=<path> --format=%B` walk the
skipped element straight past the check — #30, reopened by its own fix. The map
below is consulted per subcommand and **skips nothing by default**, because the
failure directions are not symmetric: not skipping costs a false positive, and
skipping wrongly costs a bypass.

**The residuals, stated rather than left to be found.** `shlex` resolves
quoting and command substitution is handled, but not *expansion of an
argument*: a flag assembled at run time — `F=--output=x; git log $F` — arrives
as the token `$F` after a literal `log` and is not seen. Closing that needs
the argv after expansion, which no hook is given. A subcommand assembled the
same way is refused: `F=push; git $F origin +HEAD:main` never presents `push`
to the allow-list, and `Bash(git log:*)` auto-approves the compound that
builds it. The value-flag map trails git's options the way any list does; it
is load-bearing only for false positives now, never for a bypass.

Protocol: PreToolUse, matcher `Bash`. Exit 0 and print nothing to allow; print
the deny JSON to refuse. Exit 2 would also block, but the JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed.
"""

import collections
import fnmatch
import functools
import json
import os
import re
import shlex
import sys
import threading
import traceback

# Flags that reach outside what the grant was for. Matched on a PREFIX, so
# `--exec-path=<dir>` — a directory of binaries for git to run — is the same act
# as `--exec`; an earlier form matched exactly-or-`=` and admitted it, which the
# crude substring deny had been catching all along.
#
# Three of the four write or execute. `--no-index` is the odd one and it is here
# for the same boundary in the other direction: it makes git diff two paths as
# plain files with no repository involved, so `git diff --no-index <secret>
# /dev/null` prints that file to stdout. Measured, not reasoned about — it
# printed this host's ~/.gitconfig. `Bash(git diff:*)` is granted in
# settings.json and in four commands' frontmatter, and Read is bounded by the
# harness, so without this line the read grant is wider than the Read tool it
# sits beside. Raised by Copilot against PR #13; the deny in settings.json is
# beside it as defence in depth, and this is the half that is not a speed bump.
#
# `--ext-diff` and `--textconv` are the fifth and sixth, and they are the
# ones that execute. Neither carries a command itself — they ENABLE a command
# git already has in configuration, `diff.external` and `diff.*.textconv`.
# The `git -c` list below refuses setting those keys inline and never
# refused activating an existing one, which is a boundary with the door left
# open on the other side: a developer host can carry either key already, and
# `.git/config` is writable through the redirection residual this file does
# not close (#20). Measured in a scratch repository — with
# `diff.external` set, `git diff --ext-diff` RAN it and printed its output.
# So `git diff`, `git log` and `git show`, granted everywhere as read-only,
# were host code execution one flag away. Raised by Copilot against PR #13.
#
# `--no-ext-diff` is the safe direction and stays admitted: neither the
# prefix test nor the abbreviation test matches it against these names.
FORBIDDEN_FLAGS = ("--output", "--upload-pack", "--receive-pack", "--exec",
                   "--no-index", "--ext-diff", "--textconv", "--extcmd")

# Git subcommands whose job is to run a program the tree names (#279): the
# difftool and mergetool configuration, or `-x`/`--extcmd`, is a command line.
# Nothing here runs either, so they are refused by name rather than by flag.
GIT_EXECUTING_SUBCOMMANDS = frozenset({"difftool", "mergetool"})

# Git subcommands that write the files they are given, which `READING_COMMANDS`
# keeps from being judged when `git` leads a run (#278): `git rm` and `git mv`
# are `rm` and `mv` against the same protected trees.
GIT_WRITING_SUBCOMMANDS = frozenset({"rm", "mv"})

# Programs on the reading and substitution lists with a flag that RUNS a program
# the command line names (#278, #279): `rg --pre CMD` runs CMD on every file it
# searches, `--hostname-bin` runs one for the hostname. Matched whole, with `=`
# or abbreviated, so `--pre=CMD` is refused; a bare `--pre-glob` runs nothing
# and is admitted.
#
# `sort` is the same shape: `-o FILE` and `--output` write one, and
# `--compress-program` runs one, and it is a substitution program. The names are
# matched whole, with `=`, or as GNU abbreviates a long flag (`--out`, `--compress`);
# a short flag anywhere in a cluster (`-ofile`, `-uo file`), over-refusing a cluster
# whose letter belongs to another option's argument, which is the safe direction.
EXECUTING_PROGRAM_FLAGS = {
    "rg": ("--pre", "--hostname-bin"),
    "sort": ("-o", "--output", "--compress-program"),
}

# Environment variables git EXECUTES (#281), the spellings of `git -c` and
# `--ext-diff` the global option scan cannot see because they are not options.
# Also the generic names git and rg consult when theirs is unset (`PAGER`,
# `EDITOR`, `VISUAL`, `SSH_ASKPASS`), the directories git reads configuration or
# templates from (`HOME`, `XDG_CONFIG_HOME`, `GIT_DIR`, `GIT_COMMON_DIR`,
# `GIT_TEMPLATE_DIR`), and `RIPGREP_CONFIG_PATH`, whose file may hold `--pre`.
# Matched without regard to case, as Windows reads the environment. `GIT_WORK_TREE`
# only points at files to read, and stays admitted.
GIT_EXECUTING_ENVIRONMENT = re.compile(
    r"^(?:GIT_(?:CONFIG_COUNT|CONFIG_KEY_\w+|CONFIG_VALUE_\w+|CONFIG_PARAMETERS"
    r"|CONFIG_GLOBAL|CONFIG_SYSTEM|EXTERNAL_DIFF|SSH|SSH_COMMAND|EXEC_PATH"
    r"|PAGER|ASKPASS|PROXY_COMMAND|EDITOR|SEQUENCE_EDITOR|DIR|COMMON_DIR"
    r"|TEMPLATE_DIR)"
    r"|PAGER|EDITOR|VISUAL|SSH_ASKPASS|HOME|XDG_CONFIG_HOME|RIPGREP_CONFIG_PATH"
    r"|PATH|LESS|LESSOPEN|LESSCLOSE|BASH_ENV|ENV|SHELL|GIT_CONFIG"
    r"|USERPROFILE|HOMEDRIVE|HOMEPATH)"
    r"\+?=", re.IGNORECASE)

# The only assignments admitted in a run that names git or rg (#298): none of
# them changes which program, configuration file or pager either one runs.
# Everything else is refused there, by name, so a variable nobody listed is
# refused rather than admitted. `LC_*` is matched by prefix.
INERT_ENVIRONMENT = frozenset({
    "LANG", "LANGUAGE", "TERM", "TZ", "NO_COLOR", "COLUMNS", "LINES",
    "GIT_WORK_TREE", "GIT_TERMINAL_PROMPT", "GIT_OPTIONAL_LOCKS",
})

ENVIRONMENT_REFUSAL = (
    "`{name}` is an environment variable git or rg reads in a way that can "
    "EXECUTE — a command, a pager, an ssh, an external diff or a configuration "
    "file — or one this guard has not listed as inert. It is `git -c`, "
    "`--ext-diff` or `--pre` spelled as an assignment, and those are refused; "
    "this is too."
)

# Commands whose arguments are assignments, as `export GIT_PAGER=x` is.
ASSIGNING_COMMANDS = frozenset({"env", "export", "declare", "typeset", "local", "readonly"})

# Judged against a whole element, and only on a subcommand that takes a
# repository — a branch name, a path or a commit body may carry the sequence
# without using it as a transport.
FORBIDDEN_SUBSTRINGS = ("ext::",)

# **The trees a redirection or a writing verb may not write into (#20, #26),
# and the reason this list is here rather than read off the deny rules is that
# a hook cannot see them.**
# `.claude/settings.json` denies `Edit(.claude/scripts/**)` and every editing
# command denies the same trees in its own frontmatter — and all of that binds
# the EDITING TOOLS. A `>` on any granted `Bash` command writes what every one
# of those refuses (#20), measured on this repository's own hook: `ls >
# .claude/settings.json`, `wc -l README.md > package.json` and `git log
# --oneline > package.json` were all admitted, and in a scratch directory the
# write landed.
#
# `/review-grok` answered this by denying `Bash` whole, which is available to
# exactly one command: the other four editing commands have fixed helpers as
# their API and cannot. So the rule moves here, where it binds the redirection
# rather than the tool beside it.
#
# **A hook is handed a command, never the frontmatter that granted it**, so the
# set cannot be derived at run time the way `test_harness_helpers.py` derives the
# frontmatter denies from the frontmatter. What stands instead is a test whose
# subject is this list: the suite asserts it covers `MACHINERY_TREES` and every
# tracked root file, so a new tree or a new root file fails the suite until
# somebody decides which side of the boundary it is on — the same shape
# `CLAUDE.md` already describes for the harness suite as a whole.
#
# `.vscode` is here and is not in the suite's machinery set, because
# `tasks.json` runs on folder open and this list is about writes rather than
# about what a command may edit; the suite asserts coverage in one direction
# only for exactly that reason.
#
# `.remember` is here because `.claude/settings.json` denies editing it
# globally — session state no command writes by hand — and `ls` and `wc` are
# approved just as globally, so `ls > .remember/now.md` wrote past that deny
# exactly as the trees above were written past. Raised by Copilot.
PROTECTED_TREES = frozenset({
    ".claude", ".git", ".github", ".remember", ".vscode", "android", "ios",
    "node_modules",
    # MSBuild imports `<obj>/<project>.nuget.g.props` and `.targets` unasked, and
    # a restore that finds nothing changed does not rewrite them (#311). This
    # tree is build output (`artifacts/obj` here) and no command writes it.
    "obj",
})

# **The root files, matched on the BASENAME, and the over-refusal is
# deliberate.** A target is judged lexically — this hook has no `cwd` it can
# trust and resolving one would be a second answer to the question
# `guard-edit-target.py` already answers — so `src/app/README.md` is refused
# along with `README.md`. That costs a redirection nobody makes and buys the
# nested spellings that matter: an `eslint.config.js` or a `vite.config.ts` in
# a subdirectory is loaded by EXECUTING it, and `../package.json` is the root
# file under another name. The editing tools remain the way to write one, where
# the permission rules see the path and judge it.
PROTECTED_FILES = frozenset({
    ".editorconfig", ".gitattributes", ".gitignore", ".npmrc", ".nvmrc",
    ".codeindexignore", ".mcp.json",
    "BlueprintAdmin.slnx", "Directory.Build.props", "Directory.Packages.props",
    "global.json",
    # What MSBuild and NuGet load without being asked, so a written file runs
    # under the granted `host-checks.sh` (#311). Not tracked, like `.npmrc`.
    "Directory.Build.targets", "Directory.Build.rsp", "MSBuild.rsp",
    "Directory.Solution.props", "Directory.Solution.targets",
    "NuGet.Config", "nuget.config",
    "BlueprintAdmin.slnx.targets", "before.BlueprintAdmin.slnx.targets",
    "after.BlueprintAdmin.slnx.targets",
    ".prettierrc", ".prettierrc.cjs", ".prettierrc.js", ".prettierrc.json",
    "AGENTS.md", "CLAUDE.md", "README.md",
    "angular.json", "capacitor.config.ts", "ionic.config.json",
    "eslint.config.cjs", "eslint.config.js", "eslint.config.mjs",
    "jest.config.js", "karma.conf.js",
    "npm-shrinkwrap.json", "package-lock.json", "package.json",
    "playwright.config.ts",
    "prettier.config.cjs", "prettier.config.js", "prettier.config.mjs",
    "tsconfig.app.json", "tsconfig.json", "tsconfig.spec.json",
    "vite.config.js", "vite.config.mts", "vite.config.ts",
    "vitest.config.js", "vitest.config.mts", "vitest.config.ts",
})

# The comparison copies `protected_path` reads; the sets above stay in their
# real spelling because the suite compares them against `git ls-files`.
PROTECTED_TREES_FOLDED = frozenset(name.lower() for name in PROTECTED_TREES)
PROTECTED_FILES_FOLDED = frozenset(name.lower() for name in PROTECTED_FILES)

# `git -c <key>=<value>` sets configuration for one invocation, and a long list
# of config keys are EXECUTED by git: `alias.*`, `core.pager`, `core.editor`,
# `core.sshCommand`, `core.hooksPath`, `diff.external`, `diff.*.textconv`,
# `filter.*.clean`, `credential.helper`, `sequence.editor`, `gpg.program`,
# `uploadpack.packObjectsHook`. Measured, not reasoned about:
# `git -c "alias.x=!echo PWNED" x` prints PWNED.
#
# **Enumerating the executing keys is the deny-list this repository has refused
# twice**, and git's list grows on git's schedule rather than on ours. So the
# OPTION is refused instead of its values being judged — nothing in this
# repository passes `-c` or `--config-env` to git, which is what makes that
# affordable. If a caller ever needs one, the honest change is an allow-list of
# keys, not a list of the dangerous ones.
CONFIG_OPTIONS = ("-c", "--config-env")
REPOSITORY_SUBCOMMANDS = {
    "fetch", "clone", "pull", "push", "remote", "submodule", "ls-remote",
    "archive", "bundle",
}

# Git's own options, which sit before the subcommand. Taken from git's synopsis
# rather than from the options this file happened to hit — which is how `-C` was
# missed, and then `--attr-source` in the fix for it. **This list still trails
# git's globals and that is stated rather than implied**; it is load-bearing
# only for locating a subcommand, never for the push check, which no longer asks
# where the subcommand is.
GLOBAL_VALUE_FLAGS = {
    "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env",
    "--attr-source",
}

# Per subcommand, because arity is not a property of a flag name: `-m` is a
# message for `commit` and "show merge diffs" for `log`. Absent an entry, NOTHING
# is skipped — a false positive is cheap and a bypass is not.
VALUE_FLAGS_BY_SUBCOMMAND = {
    "commit": {"-m", "--message", "-F", "--file", "-C", "--reuse-message",
               "-c", "--reedit-message", "--author", "--date", "--squash",
               "--fixup", "--pathspec-from-file"},
    "tag": {"-m", "--message", "-F", "--file", "-u", "--local-user"},
    "merge": {"-m", "--message", "-F", "--file", "-S", "--strategy"},
    "stash": {"-m", "--message"},
    "notes": {"-m", "--message", "-F", "--file"},
    "revert": {"-m", "--mainline", "-S"},
    "cherry-pick": {"-m", "--mainline", "-S"},
    "branch": {"-u", "--set-upstream-to", "--contains", "--sort"},
}

SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}

# ---- the push allow-list ---------------------------------------------------

# Options a push may carry. Anything else — including a spelling git invented
# last week, an abbreviation, or a bundle like `-fv` — is refused rather than
# checked against a list of what is dangerous.
PUSH_ALLOWED_FLAGS = {
    "-u", "--set-upstream", "-q", "--quiet", "-v", "--verbose",
    "--porcelain", "--progress", "--no-progress", "--atomic", "--no-verify",
    "-n", "--dry-run",
}
# `--follow-tags` is not in that set. It publishes every annotated tag
# reachable from the named ref, which is extra destinations this allow-list
# does not inspect — the same shape as `--all`, arriving as a flag.

# A ref this guard is willing to read: no `*`, no `+`, no `:` beyond the one
# separator, nothing that could be a pattern or an option. **Parentheses are
# admitted because `/branch` names a feature branch `feat(<scope>)/...`**, and
# a class without them made the chain's own naming table unpushable. Neither is
# a pattern to git, whose one refspec wildcard is `*`, nor to bash, where `(`
# opens an extended glob only after one of `?*+@!` — none of which this class
# admits — so a destination that matches still names exactly one branch.
# `PROTECTED_BRANCHES` is judged separately and is unaffected.
SAFE_REF = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/()-]*$")
SAFE_REMOTE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
# A subcommand this guard is willing to key the rest of the grammar off:
# letters, digits, dots, underscores, hyphens. `$F`, `$(echo push)` and `p?sh`
# are not that, and the push allow-list never sees the verb bash runs.
SAFE_SUBCOMMAND = SAFE_REMOTE

# Sources that name no destination of their own. `git push origin HEAD` updates
# whatever branch you are standing on — `main`, if you are on `main` — and a
# hook is not given the repository state to find out which.
UNRESOLVABLE_SOURCES = {"HEAD", "@", "HEAD~", "@{u}", "@{upstream}"}

PROTECTED_BRANCHES = {"main"}

# Plumbing that updates a remote ref without the verb `push`, so the protected
# branch, force and delete checks above never see it (#284). Refused by name:
# nothing this repository does needs either, and `git push` is the one spelling
# the checks can read.
#
# Besides the two push verbs: the remote helpers speak the push protocol over a
# pipe, and `subtree push` runs `git push` itself with a destination this guard
# never reads. Compared lower-cased, since Windows finds an external git command
# by a case-insensitive file name.
REF_UPDATING_PLUMBING = {"send-pack", "http-push", "receive-pack", "subtree"}

# The git globals that take no value, from git's synopsis, so a word after one
# is the subcommand and not a value to look past.
BOOLEAN_GLOBALS = {
    "-p", "--paginate", "-P", "--no-pager", "--bare", "--no-replace-objects",
    "--literal-pathspecs", "--glob-pathspecs", "--noglob-pathspecs",
    "--icase-pathspecs", "--no-optional-locks", "--no-advice", "--no-lazy-fetch",
    "--exec-path", "--html-path", "--man-path", "--info-path", "--version", "--help",
}


def verb_name(word):
    """A subcommand word as Windows resolves it: lower-cased, with trailing dots and
    spaces dropped and one executable suffix (`.exe`, `.cmd`, `.bat`, `.com`) removed.

    git finds an external command by file name, and Windows finds `git-push.exe`
    for `push.exe`, `PUSH.` and `push `. The checks below compare this, not the
    word as typed.
    """
    name = word.lower().rstrip(". ")
    for suffix in (".exe", ".cmd", ".bat", ".com"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    return name


def plumbing_verb(segment):
    """The ref-updating verb this segment runs, or None.

    Not read from `subcommand_of`, which skips only the globals this file
    lists: an unlisted value-taking one (`--super-prefix X send-pack`) would
    make `X` the verb and hide the real one. So a word right after a flag this
    file cannot place is either that flag's value or the verb, and the scan goes
    on past it; after a listed value flag, a listed boolean or a `--flag=value`
    the next word is the verb and the scan stops there, which is what keeps
    `git --no-pager log remote-fix` and `git -C d log subtree` admitted. Every
    `remote-*` helper counts as well.
    """
    after_unplaced_flag = False
    index = 0
    while index < len(segment):
        element = segment[index]
        if element.startswith("-"):
            if element in GLOBAL_VALUE_FLAGS:
                index += 2
                after_unplaced_flag = False
                continue
            after_unplaced_flag = "=" not in element and element not in BOOLEAN_GLOBALS
            index += 1
            continue
        lowered = verb_name(element)
        if lowered in REF_UPDATING_PLUMBING or lowered.startswith("remote-"):
            return element
        if not after_unplaced_flag:
            break
        after_unplaced_flag = False
        index += 1
    return None

# Heredoc introducers. The body between the introducer and its delimiter is data
# the shell hands to a command, not a command line.
# **A delimiter is a shell WORD, and matching an identifier-shaped prefix of
# one was a bypass.** `<<EOF-1` matched `EOF`, no `^EOF$` line was ever found,
# the whole tail was taken for an unterminated body — and the
# `git push origin +HEAD:main` after the real `EOF-1` line went with it.
# Measured: bash terminates on `EOF-1` and runs the push. Raised in review.
#
# `[ \t]*` rather than `\s*`, because a newline between `<<` and its delimiter
# is not a heredoc to bash either. The quote characters are spelled \x27 and
# \x22 so that neither this pattern nor anything quoting it has to escape them.
# **And a WORD may be quoted in PARTS, which matching one alternative could not
# express.** `<<E"OF"` names the delimiter `EOF` to bash and takes its body
# verbatim; the three-alternative form matched `<<E`, left `"OF"` standing where
# the subcommand goes, and `git <<E"OF" push origin +HEAD:main` was admitted
# while bash ran the push. Raised in review; verified allowed. So the word is
# one or more fragments — single-quoted, double-quoted, escaped or bare — and
# `_heredoc_delimiter` below does the quote removal the shell does.
#
# **`$'…'` is a quoting form and reading its `$` as bare was a fail-open.**
# `<<$'EOF'` names `EOF`; taking the `$` for an ordinary character made the
# delimiter `$EOF`, so a script terminating at a real `EOF` line had every
# command after it swallowed as body text — `git push origin +HEAD:main`
# included. Raised in review; verified allowed. `$"…"` is the locale form and
# is listed beside it for the same reason.
#
# **A continuation may split the delimiter itself**, and the word class has to
# say so before anything else can. `<<EO\<newline>F` names `EOF` to bash, which
# removes the pair at the input level; reading the delimiter as `EO` made the
# guard's body start a line early and end a line early, so the real command
# line was swallowed as data and `git <<EO\<newline>F push origin +HEAD:main`
# was admitted. `join_continuations` cannot help here — `strip_heredocs` runs
# on the raw command, before it, and must, because a heredoc body is not a
# command line. Raised in an adversarial audit; verified allowed, on `main` as
# well. `\\\n` leads the alternatives because `\\.` cannot match a newline.
#
# **A quoted fragment ends at an UNESCAPED quote and never spans a line.**
# `<<"E\\"OF"` names `E"OF` to bash; the fragment closed at the escaped quote,
# the scan then ran on across the newline and took the next line into the
# word, and the delimiter came out as nonsense — so `heredoc_spans` found no
# body at all. That direction happened to refuse; the mirror of it, where the
# nonsense delimiter matches a line the payload plants, swallows whatever sits
# between. Raised in review.
HEREDOC = re.compile(
    r"<<(?P<dash>-?)[ \t]*"
    r"(?P<word>(?:\\\n"
    r"|\$?\x27[^\x27\n]*\x27"
    r"|\$?\x22(?:[^\x22\\\n]|\\[^\n])*\x22"
    r"|\\[^\n]"
    r"|[^\s;&|<>()\x27\x22\\])+)"
)


def _sigil_quote(word, index):
    """Where the quote of a `$'…'`/`$"…"` at `index` starts, or None.

    Line continuations between the two are skipped, because bash removes them
    before it reads the word.
    """
    peek = index + 1
    while word[peek:peek + 2] == "\\\n":
        peek += 2
    return peek if word[peek:peek + 1] in ("'", '"') else None


def _heredoc_delimiter(word):
    """The literal delimiter `word` names, and whether its body expands.

    Bash removes the quoting from a heredoc delimiter and expands the body only
    when the word carried **no** quoting at all — and the quoting may be
    partial, which is the whole of why this is a function rather than a group
    in the pattern. `<<E"OF"`, `<<"EOF"`, `<<'EOF'` and `<<\\EOF` all name
    `EOF` and all take their bodies verbatim; only a wholly bare `<<EOF`
    expands.

    **`$'…'` decodes escapes, and this returns `None` rather than guess one.**
    A delimiter the guard gets wrong is not symmetric: too long and the body
    swallows the commands after it, which is the fail-open this whole function
    exists to close. So an ANSI-C fragment carrying a backslash — the only part
    of the form that needs decoding — makes the delimiter unknown, and
    `heredoc_spans` then opens no body at all, leaving every following line to
    be judged as the command it may be. Erring toward refusing is the direction
    that costs a false positive rather than a force push.

    The pattern admits a fragment only in complete form, so every quote opened
    here is closed and the searches below cannot fail.
    """
    out, index, quoted = [], 0, False
    while index < len(word):
        char = word[index]
        if char == "$" and _sigil_quote(word, index) is not None:
            # **A continuation between the sigil and its quote does not break
            # the pairing**, because bash removes the pair before it reads the
            # word: `<<$\<newline>'EOF'` names `EOF`. Reading the `$` as an
            # ordinary character gave `$EOF`, so the real `EOF` line terminated
            # nothing and every command after it was swallowed as body text.
            #
            # Raised in review, and answered once before it was true: the case
            # passed at the time for an unrelated reason — one of the expansion
            # readings happened to rewrite inside the body — and only stopped
            # passing when those readings were correctly stopped from rewriting
            # a body that expands nothing. A test that passes for a reason
            # nobody has checked is one that reports the wrong thing later.
            peek = _sigil_quote(word, index)
            quote = word[peek]
            close = word.index(quote, peek + 1)
            body = word[peek + 1:close]
            if quote == '"':
                # **A locale-quoted delimiter is TRANSLATED**, exactly as
                # a locale-quoted word is, and this branch was reading
                # `$"EOF"` as the literal `EOF` while
                # `undecodable_dollar_quote` refused the same construct
                # three functions along. A catalogue naming `EOF` for
                # `safe` ends the body where bash does not, and a push
                # between the two lines is swallowed or exposed depending
                # on which way the mismatch falls. Raised in review, which
                # also caught the test that had just pinned `$"EOF"` as
                # literal — the assertion and the defect landed together.
                return None, False
            # **Either sigil, and an earlier revision refused only the
            # ANSI-C one.** `<<$"E\\"OF"` names `E"OF` to bash, where
            # `word.index` finds the ESCAPED quote and derives `E\\OF` — a
            # delimiter that matches nothing, so a line the payload plants
            # can close the body early or late and take an intervening
            # push with it. Raised in review; measured, and the reasoning
            # that let the two sigils differ was that a locale quote
            # "carries double-quote semantics", which is exactly why its
            # closer is not the first quote.
            if "\\" in body:
                return None, False
            out.append(body)
            index = close + 1
            quoted = True
            continue
        if char == "'":
            close = word.index(char, index + 1)
            out.append(word[index + 1:close])
            index = close + 1
            quoted = True
            continue
        if char == '"':
            # Double quotes carry escapes, so the closer is the first UNESCAPED
            # one and `\"` contributes a quote rather than ending the fragment.
            scan, body = index + 1, []
            while scan < len(word):
                if word[scan] == "\\" and scan + 1 < len(word):
                    body.append(word[scan + 1] if word[scan + 1] in '$`"\\'
                                else word[scan:scan + 2])
                    scan += 2
                    continue
                if word[scan] == '"':
                    break
                body.append(word[scan])
                scan += 1
            out.append("".join(body))
            index = scan + 1
            quoted = True
            continue
        if char == "\\" and word[index + 1:index + 2] == "\n":
            # A continuation inside the delimiter contributes nothing and
            # quotes nothing — bash removes the pair before it reads the word.
            index += 2
            continue
        if char == "\\" and index + 1 < len(word):
            out.append(word[index + 1])
            index += 2
            quoted = True
            continue
        out.append(char)
        index += 1
    return "".join(out), not quoted


def shell_positions(command, data=()):
    """Walk `command`, yielding `(index, in_quotes, in_comment)` per character.

    One of two adapters over `_quoting`, which is where the model lives.
    """
    for index, state, _escaped in _quoting(command, data=data):
        yield index, state in ("single", "double", "data"), state == "comment"


def quote_states(command, quotes=True):
    """`command`'s quoting state at each character.

    `state[i]` is `"single"`, `"double"`, `"comment"` or `""`. A scanner that
    also needs to know where an escape sits keeps its own backslash branch:
    every caller here already had one, and they differ — a continuation join
    deletes the pair, an expansion rewrite copies it through.

    **This exists because five scanners in this file each carried their own
    copy of bash's quote rules, and none of them learned about `$'…'`.**
    `shell_positions` was made escape-aware for it, and
    `without_substitutions`, `rewriting_expansions`, `dollar_quotes`,
    `join_continuations` and `substitutions` were not — so `: $'x\'';` in
    front of a command left every one of them one quote out of step, and
    `$( )`, `${x:-push}`, a line continuation and a nested `$(git push …)`
    each walked past the pass that exists to catch it. Four of the five were
    verified allowed; all five are raised in review. **A fix that lands in one
    function and not in its siblings is this file's most-repeated failure**,
    and the answer is not a sixth careful copy.

    `quotes` is false for a heredoc BODY, where a quote is an ordinary
    character — the same flag its callers already take.
    """
    states = [""] * len(command)
    for index, state, _escaped in _quoting(command, quotes=quotes):
        states[index] = state
    return states


def _quoting(command, quotes=True, data=(), ambiguous=None):
    """Walk `command`, yielding `(index, state, escaped)` per character.

    `data` is spans this scanner must read as data rather than as shell text —
    heredoc bodies, and only `heredoc_spans` passes any. Inside one, a quote
    opens nothing and a `#` starts nothing: the characters are yielded as
    quoted, which is what every consumer of this scanner means by "not a
    command line".

    **One scanner, because both callers were defeated by the same thing.** A
    regex search for `<<` found a heredoc opener inside a COMMENT, so
    `git status # <<EOF` swallowed the real command on the next line before it
    could be judged; and a paren counter that did not know about quotes let
    `git log "$(printf ')'; git push origin +HEAD:main)"` close early, hiding
    the push in the outer token. Both raised in review, both verified allowed.

    `ambiguous`, when a list is passed, receives the index of every character
    inside a `${...}` that this walk cannot read the way bash does: a `#` at the
    start of a word and a `<<`, and, while a command substitution or backtick
    that opened inside a `${` is open (its braces are not tracked, so the depth
    is frozen until it closes), any such `#` or `<<`. Each is text in one
    reading and a comment or a
    heredoc in another, and a guess either way hides a live command (#285), so
    `ambiguous_expansion` refuses the command rather than relying on one. A
    substitution inside `${` with no `#` or `<<` after it is judged as before.

    `state` is `"single"` inside `'…'`, `"double"` inside `"…"`, `"comment"`
    in a comment, `"data"` inside one of `data`'s spans and `""` elsewhere.
    Comments start at an unquoted `#` that begins a word and end at the
    newline — which is bash's rule, and the reason `git log --grep=#x` is not
    a comment.

    **An ANSI-C word takes a backslash and an ordinary single-quoted one does
    not**, and reading `$'…'` by the ordinary rule desynchronised every
    consumer of this scanner from the position it was on. `$'''` is the
    one-character word `'` to bash — the escaped quote does not close it — so
    `$''' ; git 2>&1 push origin +HEAD:main` runs the push. Read by the
    ordinary rule the word closes at the escaped quote, the quote after it
    opens one that never closes, and the rest of the line is `in_quotes`: so
    `redirection_spans` left `2>&1` standing, `is_boundary` read the glued
    `>&` as a run boundary, and `git` was severed from its own subcommand.
    Raised in review; verified allowed. `$"…"` needs nothing, because a
    locale-quoted word already follows the double-quoted rule this scanner
    applies to it.
    """
    # **Not copied and not sorted**: `heredoc_spans` appends to this list
    # while consuming the generator, and every span it appends starts ahead of
    # the cursor, so the order holds by construction.
    single = double = comment = False
    # Whether the single quote now open was introduced by a `$`, and whether
    # the character just yielded was an unquoted, unescaped `$`.
    ansi_c = dollar = False
    # How many `${` are open and unclosed. Inside one a `#` is text, never a
    # comment (#285): `ls ${x:- #}; git push origin +HEAD:main` runs the push,
    # and reading `#}; git push ...` as a comment stripped it before any check.
    # Over-counting only keeps more of the command in view, which is the safe
    # direction; an unclosed `${` leaves the rest of the command judged.
    brace_depth = 0
    # The same for a `${` inside a double-quoted string, where the depth is lost
    # for good (`string_braces` below).
    lost = False
    # How deep a command substitution that opened inside a `${` is, and whether
    # it is a backtick one. Its own braces are not followed (`${x:-$(echo })}`),
    # so `brace_depth` is frozen until its closing parenthesis or backtick, where
    # the `${` it sat in is read again.
    sub, tick, sub_start = 0, False, 0
    # The same for a `${` inside a double-quoted string, where a `"` after the
    # `:-` opens a quote of its own rather than ending the string, and the second
    # one closes it: `echo "${x:-" #"}"; git push ...` prints ` #` and then runs
    # the push, measured, while reading the first inner quote as the end of the
    # string made the rest of the line a comment. One frame per open `${`, each
    # holding whether a quote of its own is open, since a `}` inside that quote
    # closes nothing and a `${` inside it opens a frame of its own: a single flag
    # lost its place at `"${x:-"${y:-a}"}"` and left the rest of the line quoted.
    string_braces = []
    # Whether a command substitution or backtick has opened inside the double-
    # quoted string now open. Its body (a heredoc with a stray `${` in it) is not
    # followed, so any `${` after it in the same string cannot be framed.
    string_command = False
    # **Whether a `#` begins a WORD, tracked rather than inferred from the
    # previous character.** The old test read `command[index - 1] in " \t…"`,
    # which cannot tell a separating space from an escaped one: in
    # `git log --grep=foo\\ #bar;git push origin +HEAD:main` bash keeps
    # `#bar` inside the `--grep` argument and runs the push, while the guard
    # read a comment and stripped the lot. Measured with a `git` shim. Raised
    # in review.
    at_word_start = True
    index = 0
    # Whether this character is the one the backslash before it escapes.
    pending = False
    # A cursor rather than a search: this walk is monotonic, so the spans are
    # consumed in order. Searching them per character made a command carrying
    # 200 heredocs ten times slower, and a quadratic path in this file is the
    # shape that produced the memoisation fix.
    cursor = 0
    while index < len(command):
        while cursor < len(data) and data[cursor][1] <= index:
            cursor += 1
        if cursor < len(data) and data[cursor][0] <= index:
            while index < data[cursor][1] and index < len(command):
                yield index, "data", False
                index += 1
            at_word_start = True
            dollar = False
            continue
        char = command[index]
        if comment:
            if char == "\n":
                comment = False
                at_word_start = True
            else:
                yield index, "comment", False
                index += 1
                continue
        if not comment:
            if single:
                if ansi_c and char == "\\" and index + 1 < len(command):
                    # Both characters, for `strip_comments`' reason below: a
                    # consumer rebuilds text from these positions.
                    yield index, "single", False
                    yield index + 1, "single", True
                    index += 2
                    dollar = False
                    continue
                if char == "'":
                    single = ansi_c = False
            elif double:
                if char == "\\" and index + 1 < len(command):
                    # **Both characters, because a consumer rebuilds text from
                    # these positions.** Yielding only the backslash made
                    # `strip_comments` DELETE the escaped character, so
                    # `git log "$(printf \); git push …)"` lost its `)` and
                    # changed shape on its way through the guard. A scanner that
                    # silently edits its input is worse than one that misreads
                    # it, because every later stage inherits the edit.
                    yield index, "double", False
                    yield index + 1, "double", True
                    index += 2
                    dollar = False
                    continue
                # `"$${y:-"` is not a `$$` pair followed by text: bash reads the
                # second `$` with the `{` as an expansion and refuses the whole
                # line (measured), so a frame opens here for every `${`.
                if char == "$" and command.startswith("{", index + 1):
                    if string_command and ambiguous is not None:
                        ambiguous.append(index)
                    yield index, "double", False
                    yield index + 1, "double", False
                    index += 2
                    string_braces.append(False)
                    continue
                if char == "`" or (char == "$" and command.startswith("(", index + 1)):
                    string_command = True
                    if string_braces:
                        lost = True
                if string_braces:
                    if char == '"':
                        string_braces[-1] = not string_braces[-1]
                    elif char == "}" and not string_braces[-1]:
                        string_braces.pop()
                elif char == '"':
                    double = False
                    string_command = False
            elif char == "\\" and index + 1 < len(command):
                # An unquoted backslash escapes the next character, so that
                # character is ordinary text — a space included, and an escaped
                # space separates nothing.
                yield index, "", False
                yield index + 1, "", True
                index += 2
                at_word_start = False
                dollar = False
                continue
            elif char == "'" and quotes:
                single = True
                ansi_c = dollar
                at_word_start = False
            elif char == '"' and quotes:
                double = True
                at_word_start = False
            elif char == "{" and dollar and not sub:
                brace_depth += 1
                at_word_start = False
            elif char == "}" and brace_depth and not sub:
                brace_depth -= 1
                at_word_start = False
            elif ambiguous is not None and (brace_depth or lost or sub) and (
                    (char == "#" and at_word_start)
                    or command.startswith("<<", index)):
                # Inside `${`, or after its depth was lost: a `#` after a space
                # is text to bash but a comment to a guess, and a `<<` is text
                # where a guess opens a heredoc. Recorded, and the command
                # refused by `ambiguous_expansion`.
                ambiguous.append(index)
                at_word_start = char in METACHARACTERS
            elif sub and tick:
                # Inside a backtick body the next backtick closes it; its own
                # braces and parentheses are not followed.
                if char == "`":
                    sub, tick = 0, False
                at_word_start = char in METACHARACTERS
            elif sub and ((char == "{" and dollar) or char == "`"):
                # A `${` or backtick inside the substitution has a close of its
                # own that the parentheses count does not follow (`${b:-)}`
                # holds a `)` that is text), so the depth cannot be trusted.
                lost = True
                at_word_start = True
            elif sub and char in "()":
                sub += 1 if char == "(" else -1
                at_word_start = char == "("
                if not sub and re.search(
                        r"\bcase\b", command[sub_start:index]):
                    # A `case` pattern's `)` closes nothing, so the count
                    # above cannot be trusted for this body.
                    lost = True
            elif brace_depth and not sub and ((char == "(" and dollar) or char == "`"):
                # A command substitution inside `${` has braces of its own
                # (`${x:-$(echo })}`) that this walk does not follow. It is
                # followed by its parentheses instead, and the `${` it sat in
                # is read again at its closing parenthesis (#296).
                sub, tick, sub_start = 1, char == "`", index
                at_word_start = True
            elif char == "#" and at_word_start and not brace_depth:
                comment = True
                yield index, "comment", False
                index += 1
                dollar = False
                continue
            elif char in METACHARACTERS:
                at_word_start = True
            else:
                at_word_start = False
        yield index, ("single" if single else "double" if double
                      else "comment" if comment else ""), pending
        pending = False
        dollar = char == "$" and not (single or double or comment)
        index += 1
    if string_braces and ambiguous is not None:
        # A `${` inside a double-quoted string that nothing closed: the rest of
        # the command was read as that string's text. Bash closes the string at
        # the quote when the `${` sat in a body this walk does not follow
        # (`"$(cat <<'EOF' ... use ${ here ... EOF )"`), so a command after it
        # would be hidden as quoted text. Recorded, and refused.
        ambiguous.append(len(command))


def _escaped(command, index):
    """Whether the character at `index` is escaped: an odd run of backslashes before it."""
    run = 0
    while index - run > 0 and command[index - run - 1] == "\\":
        run += 1
    return run % 2 == 1


def heredoc_spans(command):
    """Every heredoc body in `command`, as `(start, end, expands)`.

    `start` is just past the introducer and `end` just past the closing
    delimiter line, so `command[start:end]` is everything the shell hands over
    as data rather than reading as a command line.

    **`expands` is the half this used to throw away**, and throwing it away was
    a bypass rather than an imprecision. `<<'EOF'` and `<<"EOF"` hand the body
    over verbatim; a bare `<<EOF` performs substitution and parameter expansion
    on it first. A guard that treats both as inert misses a live
    `$(git push origin +HEAD:main)` in the second, and a guard that treats both
    as executable refuses an honest commit quoting one in the first. Only the
    delimiter's quoting tells them apart, and `HEREDOC` has always captured it.

    **An opener is only an opener in executable position.** A `<<EOF` inside a
    comment or inside quotes is text, and treating it as an operator let
    `git status # <<EOF` delete the command on the following line — the guard
    removing the very thing it exists to read.
    """
    # **A body is data, and its quotes are not the command line's.** An
    # apostrophe in one used to open a quote that ran to the end of the
    # command, so every later opener sat `in_quotes`, was skipped, and its
    # body was left standing to be tokenised as commands. Found by hitting it:
    # writing four replies to disk with `cat > f <<'EOF'` heredocs was refused
    # because a body quoting `bash -c` reached the evaluator scan. Over-refusal
    # in every direction probed — a push after such a body was refused before
    # and after — which is how it survived this long.
    #
    # **`data` is handed to the scanner and appended to WHILE it walks**, which
    # is what makes this one pass. Feeding the spans back between whole passes
    # instead recovers exactly one body per pass, because each newly visible
    # body breaks the state again at its own apostrophe: measured at n+1 passes
    # for n heredocs, which is the quadratic shape this file already treats as
    # a fail-open by timeout. The scanner consumes `data` through a cursor and
    # this loop only ever appends spans that start ahead of it, so the list is
    # sorted by construction and the walk stays monotonic.
    data, spans, pending = [], [], 0
    for index, in_quotes, in_comment in shell_positions(command, data):
        if in_quotes or in_comment:
            continue
        if not command.startswith("<<", index):
            continue
        if _escaped(command, index):
            # A `<` the shell was told to read as a character opens no heredoc
            # (#276): the lines after it are commands and must be judged as
            # such. A shift inside `(( ))` is not counted here but refused by
            # `ambiguous_arithmetic` (#277), because a count of `((` that bash
            # does not share hides a live command in either direction.
            continue
        # **`<<<` is a here-string, and it fed a push straight past this.**
        # The bare-delimiter alternative excludes `<`, so no opener matched at
        # the FIRST character of `<<<EOF` — and the scan then reached the
        # second one, where `<<EOF` matched perfectly. `cat <<<EOF` passes the
        # word `EOF` on stdin and the next line is an ordinary command:
        # measured, `EOF` is printed and the push runs. Raised in review.
        #
        # Two tests rather than one, because the operator has two ends. An
        # index inside a run of `<` is not the start of an operator, and an
        # operator that continues past `<<` is not a heredoc.
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        match = HEREDOC.match(command, index)
        if not match:
            continue
        # One parse of the delimiter word, quote removal included —
        # `<<\EOF` is a quoted delimiter to bash the same way `<<'EOF'`
        # is, and `<<E"OF"` is one in parts.
        delimiter, expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            # A delimiter this file cannot decode opens no body, so the
            # lines after it stay commands and are judged as such.
            continue
        intro_end, dash = match.end(), bool(match.group("dash"))

        # An introducer sitting inside an earlier body is body text, not an
        # opener — which the scanner now settles by refusing to walk a body at
        # all, so the containment test that used to stand here is gone rather
        # than kept as a second answer to one question. Two heredocs stacked on
        # ONE line both introduce before either body starts, and that is still
        # `pending`'s job below rather than an ordering test's.

        # **A body begins on the NEXT LINE, and taking it to begin at the
        # introducer was a third admitted force push.** Everything between the
        # introducer and that newline is still command line, so
        # `cat <<'A' ; git push origin +HEAD:main` had the push swallowed as
        # data and the hook returned nothing. Verified under bash: it runs.
        newline = command.find("\n", intro_end)
        if newline == -1:
            # An introducer with no line after it opens no body at all.
            continue

        # Stacked bodies queue: the second starts where the first terminated,
        # which is past its own line break.
        start = max(newline + 1, pending)
        # **The terminator is the delimiter and nothing else.** `^\s*…\s*$`
        # accepted an indented or trailing-spaced line, and bash accepts
        # neither — only `<<-` strips leading TABS, and no form ignores
        # trailing whitespace. Measured: a heredoc body containing a line
        # `  EOF` prints it and keeps going. So an ordinary commit body that
        # indents the word had its remaining lines exposed as commands, which
        # is a false positive on exactly the file this repository writes most.
        # Raised in review.
        terminator = (
            rf"^\t*{re.escape(delimiter)}$" if dash
            else rf"^{re.escape(delimiter)}$")
        closing = re.search(terminator, command[start:], re.MULTILINE)
        if closing is None:
            # **No span, so nothing is stripped, and the fail direction is the
            # point.** A delimiter this guard cannot find means one of two
            # things: the heredoc really is unterminated, in which case the
            # tail is data and scanning it over-refuses a malformed command; or
            # the delimiter was read wrongly, in which case the tail holds
            # commands. Dropping it served the first and hid the second, and
            # the second is how `<<EOF-1` walked a push past this file.
            # Scanning is wrong only in the safe direction.
            break
        pending = start + closing.end()
        spans.append((start, pending, expands))
        data.append((start, pending))
    return spans


def strip_heredocs(command):
    """`command` with every heredoc BODY removed, delimiters included.

    A heredoc body is an argument, and parsing it as a command line is how the
    guard came to refuse an honest commit that quoted a push. The introducer is
    left in place so the rest of the line still tokenises.
    """
    out, cursor = [], 0
    for start, end, _expands in heredoc_spans(command):
        out.append(command[cursor:start])
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def strip_comments(command):
    """`command` with every shell COMMENT removed, newlines kept.

    **bash's rule, not `shlex`'s, and the difference is a force push.**
    `shlex.shlex` sets `commenters = "#"` and honours it at any character
    position, so `git log --grep=#x ; git push origin +HEAD:main` tokenised to
    three tokens and the push vanished with the rest of the line — admitted, and
    verified running under bash, which starts a comment only where `#` begins a
    word. The lexer's comment handling is switched off in `offence` and this
    runs instead, over the scanner that already implements that rule for
    heredoc openers.
    """
    return "".join(
        command[index]
        for index, _in_quotes, in_comment in shell_positions(command)
        if not in_comment
    )


def ambiguous_arithmetic(command):
    """Whether `command` has an unquoted `((` and an unquoted `<<` (#277).

    Inside `(( ))` and `$(( ))` a `<<` is a left shift, and nothing opens a
    heredoc; outside, the same two characters hide every line to the delimiter.
    Telling them apart means parsing bash's arithmetic grammar: `(( 1 ) )` is two
    subshells, `$(( (1+(2)) <<EOF ))` is arithmetic whose nested parentheses end
    a naive count early, and an unmatched `((` is read as subshells. A counter
    that was wrong in either direction either hid a live command or let an
    undecodable delimiter past the refusal beside this one, so the combination is
    refused instead of counted. No command this repository writes needs both.
    Quoted text, comments and heredoc bodies do not count.
    """
    bodies = [(start, end) for start, end, _ in heredoc_spans(command)]
    paren = shift = False
    # Any unquoted, unescaped `[` attached to a word seen so far: an array
    # subscript (`a[1<<EOF]=1`)
    # is arithmetic as well, and bash reads its `<<` as a shift and runs the next
    # line. Never matched against a `]`: an escaped one, one inside a `$(`, and one
    # a later subscript closes all defeated a count, so any `[` before a `<<` is
    # enough to refuse.
    bracket = False

    for index, in_quotes, in_comment in shell_positions(command, bodies):
        if in_quotes or in_comment:
            continue
        if command.startswith("=(", index) or command.startswith("+=(", index):
            # A compound assignment: every element may carry a subscript, spaced
            # or not (`a=( [ 1<<EOF]=1 )`), and the element's shape is not worth
            # parsing for a command nobody writes next to a heredoc.
            bracket = True
        elif (command.startswith("[", index) and index > 0
                and not _escaped(command, index)
                and (command[index - 1].isalnum() or command[index - 1] in "_]"
                     or (command[index - 1] in " \t\n;&|("
                         and command[index + 1:index + 2] not in ("", " ", "\t", "\n", "[")))):
            # `name[`, or a `[` at a word start with something other than a space
            # after it: the shapes of a subscript. `[ -f x ]`, `[[ ... ]]` and
            # `*.[ch]` are not.
            bracket = True
        elif bracket and command.startswith("<<", index) and not _escaped(command, index):
            return True
        if command.startswith("$[", index):
            # The legacy arithmetic expansion, where `<<` is a shift as well.
            paren = True
        elif command.startswith("(", index):
            # `((` however it is spelled: bash joins `(\<newline>(` before it
            # reads either.
            after = index + 1
            while command.startswith("\\\n", after):
                after += 2
            if command.startswith("(", after):
                paren = True
        elif command.startswith("<<", index) and not _escaped(command, index):
            shift = True

    return paren and shift


def ambiguous_expansion(command):
    """Whether `command` has text inside `${...}` this guard cannot read (#285).

    See `_quoting`'s `ambiguous`: a `#` after a space or a `<<` inside a
    parameter expansion, or after a command substitution opened in one. Honest
    commands do not write them and every one is a way to make a stripped comment
    or a hidden heredoc body out of a live command, so it is refused, not
    interpreted.
    """
    bodies = [(start, end) for start, end, _ in heredoc_spans(command)]
    found = []
    for _ in _quoting(command, data=bodies, ambiguous=found):
        pass

    return bool(found)


def undecodable_heredoc(command):
    """Whether a heredoc names a delimiter this file cannot read.

    **"Open no body and let the lines be judged" was the wrong fail-safe, and
    review took it apart.** The reasoning was that a body left unstripped is
    read as commands, which refuses rather than admits — true only while the
    command still tokenises. A body carrying an unmatched quote sends `offence`
    down its `ValueError` path, and that fallback scans for forbidden flags and
    `ext::` alone: it does not enforce the push allow-list, so
    `git commit -F - <<$'E\\x4fF'` with such a body admitted a force push.
    Measured.

    So an undecodable delimiter is refused outright rather than worked around.
    The alternative is decoding every ANSI-C escape bash supports, which is a
    list that trails bash's — the shape this file refuses elsewhere — and each
    gap in it would reopen exactly this hole.

    The scan asks `shell_positions` where the `<<` is, so a delimiter quoted
    inside an argument is not one of these; the two guards below are
    `heredoc_spans`', for the same reasons it states.
    """
    # **The bodies are computed FIRST and handed to the scanner**, which is
    # the same fix `heredoc_spans` took one function above and the same
    # oversight arriving in the function beside it: an apostrophe in an earlier
    # body left the scanner in quote state, so a later undecodable opener
    # looked quoted and this refusal never fired. Raised in review.
    bodies = heredoc_spans(command)
    quoted = set()
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in bodies]):
        if in_quotes or in_comment:
            quoted.add(index)
    # **Without the escape exclusion `heredoc_spans` makes (#276), on purpose.**
    # That is a judgement about where a body starts; suppressing this refusal on
    # it would let an undecodable delimiter through to the tokenising path that
    # does not enforce the push allow-list. When in doubt this refuses.
    # **A `<<` inside a heredoc BODY is data, not an opener**, and reading one
    # as an opener refused an innocent filing: a body quoting `<<$'E\\x4fF'` —
    # documentation of this very mechanism — was rejected as an undecodable
    # delimiter. `heredoc_spans` is what knows where a body is, which is why it
    # is asked above rather than here.
    for match in HEREDOC.finditer(command):
        index = match.start()
        if index in quoted:
            # A body is among the spans handed to the scanner above, so an
            # opener inside one arrives quoted and this is where it stops.
            # **The containment test that used to stand here as well was the
            # quadratic** — 3,200 heredocs meant ten million comparisons, and
            # the hook's timeout is empty stdout, which is non-blocking. Raised
            # in review against `stdin_scripts`, where the same test sat for
            # the same reason; this copy was found by profiling the fix.
            continue
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        delimiter, _expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            return True
    return False


def expansion_end(command, start):
    """The end of the parameter expansion at `start`, or None if there is none.

    **The special parameters are expansions too**, and a scan that accepted
    only `[A-Za-z0-9_]` never saw them: `$@`, `$*` and `$!` are empty in the
    shell Claude Code runs commands in — no positional parameters, no
    background job — so `git $@push origin +HEAD:main` closes up into a force
    push, and `--out$@put=` and `ext$@::` reopen the other two checks the same
    way. Found by an adversarial audit; live on `main`.

    `$#`, `$?`, `$$`, `$-` and `$0` are deliberately absent: each expands to
    something non-empty, so none of them can join two words.
    """
    if not command.startswith("$", start):
        return None
    if command.startswith("${", start):
        close = _closing_brace(command, start + 2)
        return None if close is None else close + 1
    if command[start + 1:start + 2] in ("@", "*", "!"):
        return start + 2
    scan = start + 1
    while scan < len(command) and (command[scan].isalnum()
                                   or command[scan] == "_"):
        scan += 1
    return scan if scan > start + 1 else None


def glued(command, start, end):
    """Whether `command[start:end]` touches other characters of its own word.

    A word boundary is whitespace, a metacharacter, or the end of the string —
    so an expansion standing alone as `$BRANCH` is not glued, and the `${x}` of
    `--out${x}put=` is. This is the whole of the line between an expansion
    whose emptiness closes a word up and one that simply supplies a value.

    **A quote is NOT a boundary**, and counting one as such left half of this
    open: `git $x'push' origin +HEAD:main` runs the push, because quoting ends
    no word in bash — `'pu'$x'sh'` is one word too. Found by an adversarial
    audit after the `${x}` half had been closed, which is this file's own
    lesson about fixing the case in front of you rather than the grammar
    behind it.
    """
    def boundary(position):
        if position < 0 or position >= len(command):
            return True
        return command[position] in METACHARACTERS

    return not (boundary(start - 1) and boundary(end))


def without_substitutions(command):
    """`command` with every command substitution deleted rather than tokenised.

    **A substitution that prints nothing leaves the words around it joined**,
    and that is quote removal rather than run-time content: the dangerous
    string is literally in the source. `git $( )push origin +HEAD:main` runs
    the push — measured — while `shlex(punctuation_chars=True)` emitted `(` and
    `)` as their own tokens, `command_runs` ended the run there, and the second
    run held no `git` token for `git_segments` to find. The same shape hid
    `--out$( )put=` and `ext$( )::`, so it reopened all three checks at once.
    Raised in an adversarial audit; verified allowed, on `main` as well.

    `word_end` already implements exactly this rule — a substitution is part of
    the word it sits in — but only for a redirect target. Judging this string
    **beside** the ordinary one is the general form: one reading is what bash
    does when the substitution prints something, the other is what it does when
    it prints nothing, and both have to be safe.

    **A parameter expansion is deleted only where it is GLUED into a word**,
    and the line between the two cases is the one the paragraph above draws.
    `git ${x}push origin +HEAD:main` and `git log --out${x}put=/tmp/probe` run
    exactly as their `$( )` spellings do — the dangerous string is literally in
    the source and only an empty expansion is needed to close the word up. But
    `git push origin $BRANCH` is traffic this repository writes, and deleting a
    WHOLE word would refuse an honest push for naming no destination. So the
    test is adjacency: an expansion touching other characters of its own word
    goes, one standing alone stays. Raised in an adversarial audit, which
    pointed out that the residual named in `docs/harness-boundaries.md` is
    about a value assembled at run time — `F=--output=x; git log $F` — and that
    this is not that.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if command.startswith("$(", index):
            close = _closing_paren(command, index + 2)
            if close is None:
                break
            index = close + 1
            continue
        if char == "`":
            close = index + 1
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == "`":
                    break
                close += 1
            if close >= len(command):
                break
            index = close + 1
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                # **An unbalanced `${` must END the scan, the way `$(` and a
                # backtick already do.** Advancing one character and rescanning
                # from the next `${` is quadratic: `"${" * 20000` took the hook
                # past its 60-second timeout, and a hook that produces no
                # output in time is non-blocking — fail-open by exhaustion
                # rather than by misreading. Found by an adversarial audit.
                break
            if end is not None and glued(command, index, end):
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out) + command[index:] if index < len(command) else "".join(out)


def outside_verbatim(command, reading):
    """`reading` applied to `command` except inside a NON-expanding body.

    **A quoted heredoc body expands nothing**, so rewriting one is inventing
    text the shell will never produce. The readings were run over the raw
    command, and a body line reading `${x:-EOF}` was rewritten into an early
    terminator — after which the rest of an innocent filing was read as
    commands and refused. Raised in review; measured.

    An expanding body is left to the reading, because bash does expand there.
    """
    spans = [(start, end) for start, end, expands in heredoc_spans(command)
             if not expands]
    if not spans:
        return reading(command)
    out, cursor = [], 0
    for start, end in spans:
        out.append(reading(command[cursor:start]))
        out.append(command[start:end])
        cursor = end
    out.append(reading(command[cursor:]))
    return "".join(out)


def rewriting_expansions(command, replace):
    """`command` with each parameter expansion put through `replace`.

    `replace(text)` is given the expansion as written and returns what to put
    in its place, or None to leave it alone. Single-quoted regions are left
    untouched, because a `$` is literal there.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                break
            if end is not None:
                written = replace(command[index:end])
                out.append(command[index:end] if written is None else written)
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out)


def splitting_expansions(command):
    """`command` with every parameter expansion read as WHITESPACE.

    **An expansion can split one word into several, and nothing here modelled
    that.** The whole expansion model was "an empty one joins its neighbours";
    the converse is `${IFS}`, which holds a space by default, so
    `git push${IFS}origin +HEAD:main` is the entire force push written as one
    `shlex` token. Found by an adversarial audit; live on `main`.

    Read beside the other readings rather than instead of them: an expansion is
    empty, or whitespace, or its own default text, and the command is only safe
    if it is safe under all of them.
    """
    return rewriting_expansions(command, lambda _text: " ")


# `${name:-word}` and its family. The operator decides when the default is
# used; every one of them can put `word` on the command line.
DEFAULTED = re.compile(r"^\$\{[^{}:=?+-]*(?::?[-=?+])(?P<word>.*)\}$", re.DOTALL)


def defaulted_expansions(command):
    """`command` with every `${name:-word}` read as its `word`.

    **This is not the residual the documentation already names.** That one is a
    value assembled at run time — `F=--output=x; git log $F` — which no hook is
    given. Here the dangerous text is literally in the source and an unset
    variable is the default state of the shell, so `git ${x:-push} origin
    +HEAD:main` is a force push written in plain sight. Found by an adversarial
    audit; live on `main`.
    """
    def written(text):
        match = DEFAULTED.match(text)
        return None if match is None else match.group("word")

    return rewriting_expansions(command, written)


# A brace expansion that yields exactly one word is pure obfuscation of the
# text inside it, and `{`/`}` are in neither `METACHARACTERS` nor
# `PUNCTUATION`, so `p{u..u}sh` survived as one opaque token.
BRACE = re.compile(r"\{(?P<from>[^{}.,\s]+)(?:\.\.(?P<to>[^{}.,\s]+)|,(?P<rest>[^{}]*))\}")


def brace_expanded(command):
    """`command` with each brace expansion read as its first alternative.

    A single-element range — `p{u..u}sh` — is exactly `push` to bash, and a
    list takes its first word, which is the reading that hides a literal.
    Found by an adversarial audit; live on `main`.
    """
    def written(match):
        if match.group("to") is not None:
            return match.group("from") if match.group("to") == match.group("from") else match.group(0)
        return match.group("from")

    return BRACE.sub(written, command)


def dollar_quotes(command):
    """Every `$'…'` and `$"…"` in `command`, as `(start, end, ansi_c)`.

    **These are QUOTING FORMS and `shlex` has no rule for either**, so the `$`
    stayed glued outside the quote and the token was `$git` rather than `git`.
    `program_name` then matched nothing, `git_segments` yielded no segment at
    all, and every check that lives inside that loop — the push allow-list, the
    forbidden flags, `ext::` — was skipped at once. Measured on bash 5.2.26:
    `$'git' push origin +HEAD:main`, `$"git" …`, `$'g'it …` and
    `git p$'ush' …` all run the push, and all were admitted here and on `main`.
    Raised in an adversarial audit.

    `end` is just past the closing quote, and `ansi_c` says which form it is,
    because only `$'…'` decodes escapes.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    found, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] in ("single", "double"):
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            index += 2
            continue
        if (char == "$"
                and command[index + 1:index + 2] in ("'", '"')):
            # **Neither form is a quoting form INSIDE double quotes**, and
            # missing that broke this three ways at once. To bash
            # `"regex $'\\d' matches"` is an ordinary message about a regex —
            # it was refused. `"$'\\x22'"` was decoded and re-emitted as a
            # single-quoted word *inside* the surrounding double quotes, which
            # unbalanced the line, sent it to the `ValueError` path and let
            # `git p''ush origin +HEAD:main` through beside it. And `"a$"`
            # closed at the wrong quote, swallowing the rest of the line into
            # one word. All three raised in an adversarial audit; all three
            # this branch's own doing.
            #
            # **Escape-aware, like every other closer in this file.** A plain
            # `find` closed `$"\"'"` on the ESCAPED quote, resumed inside the
            # string, read the `'` there as opening single quotes, and from
            # then on saw nothing — so a later `$'push'` was never un-sigilled
            # and `git $'push' origin +HEAD:main` was admitted. That is the
            # `$'\''` desync of the round before, in the sibling form. Raised
            # in an adversarial audit.
            quote = command[index + 1]
            close = index + 2
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == quote:
                    break
                close += 1
            if close >= len(command):
                break
            found.append((index, close + 1, quote == "'"))
            index = close + 1
            continue
        index += 1
    return found


ANSI_C_SIMPLE = {
    "a": "\a", "b": "\b", "e": "\x1b", "E": "\x1b", "f": "\f", "n": "\n",
    "r": "\r", "t": "\t", "v": "\v", "\\": "\\", "'": "'", '"': '"', "?": "?",
}


def decode_ansi_c(body):
    """The text `$'<body>'` names, or None where an escape is not decodable.

    **Refusing every escape was safe and cost too much.** The first form of
    this refused any `$'…'` carrying a backslash, which took `echo $'\\n'`,
    `printf $'\\t'` and `grep -n $'\\t' file.txt` with it — ordinary traffic
    that has nothing to do with git, refused by a git guard. Raised in an
    adversarial audit.

    Decoding instead is safe **because the list only decides how much honest
    traffic is admitted, never whether a bypass gets through**: an escape this
    does not know returns None and the command is refused, so a gap costs a
    false positive rather than a force push. That is the opposite direction
    from the deny-lists this file refuses elsewhere, and it is why a list is
    affordable here.
    """
    out, index = [], 0
    while index < len(body):
        char = body[index]
        if char != "\\":
            out.append(char)
            index += 1
            continue
        if index + 1 >= len(body):
            return None
        escape = body[index + 1]
        if escape in ANSI_C_SIMPLE:
            out.append(ANSI_C_SIMPLE[escape])
            index += 2
            continue
        if escape in "01234567":
            # **`\\0nnn` counts its three digits AFTER the zero**, and reading
            # the zero as one of them made `$\'\\0165\'` the two characters
            # `\x0e5` where bash gives `u` — so `git p$\'\\0165\'sh origin
            # +HEAD:main` was a push the guard could not see. Raised in review;
            # verified allowed. The bare `\\nnn` form keeps its own count.
            first = index + 2 if escape == "0" else index + 1
            digits = body[first:first + 3]
            while digits and not all(d in "01234567" for d in digits):
                digits = digits[:-1]
            out.append(chr(int(digits, 8) & 0xFF) if digits else "\0")
            index += (first - index) + len(digits)
            continue
        if escape in "xuU":
            width = {"x": 2, "u": 4, "U": 8}[escape]
            digits = body[index + 2:index + 2 + width]
            while digits and not all(d in "0123456789abcdefABCDEF" for d in digits):
                digits = digits[:-1]
            if not digits:
                return None
            # **`chr` raises above 0x10FFFF, and a hook that raises fails
            # OPEN.** `$'\\UFFFFFFFF'` took the process down with an
            # `OverflowError`: exit 1, empty stdout, which `PreToolUse` treats
            # as a non-blocking error, so the command ran. Found by an
            # adversarial audit, and it is the worst shape a defect in this
            # file can take — every refusal in it is reached by returning a
            # string, and none of that happens after a traceback.
            point = int(digits, 16)
            if point > 0x10FFFF:
                return None
            out.append(chr(point))
            index += 2 + len(digits)
            continue
        if escape == "c":
            if index + 2 >= len(body):
                return None
            # **`str.upper()` is not length-preserving in Unicode, and `ord`
            # raises on what it returns.** `ß` upper-cases to `SS`, and
            # `$'\cß'` took the hook down with a `TypeError` — exit 1, empty
            # stdout, which `PreToolUse` treats as non-blocking, so the command
            # ran. `ﬁ`, `ŉ`, `ǰ`, `ΐ`, `ẖ` and `ẚ` do the same. Found by an
            # adversarial audit; a regression against `main`, introduced with
            # the decoder, and the second crash this file has had from
            # assuming a character-wise operation stays one character.
            control = body[index + 2]
            folded = control.upper()
            if len(folded) != 1:
                return None
            out.append(chr(ord(folded) ^ 0x40))
            index += 3
            continue
        return None
    # **A NUL truncates the word in bash, and keeping one changed what the
    # word said.** `$'a\\0b'` is the single byte `a`, so `git p$'\\0'ush` is
    # `git push` — measured — and the hook was holding a NUL in the middle of a
    # token nothing would match. Truncating models the shell exactly, where
    # refusing would have been the cruder answer. Found by an adversarial
    # audit.
    text = "".join(out)
    return text.split("\0", 1)[0]


def single_quoted(text):
    """`text` as a single-quoted shell word, whatever it contains."""
    return "'" + text.replace("'", "'\"'\"'") + "'"


def unreadable_dollar_quote(command):
    """Why `command`'s `$'…'` or `$"…"` cannot be read, or None.

    **Two different reasons, and one sentence for both said the wrong thing.**
    A plain `$"safe"` carries no escape at all; it is refused because its
    translation is a lookup in a catalogue this hook is not given. Reporting
    that as an undecodable escape tells a caller to go looking for one, in a
    command that has none. Raised in review.
    """
    for _start, _end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return (
                "a `$\"…\"` is a translated string, so what the word says is "
                "decided by a message catalogue this guard is not given; "
                "refusing rather than reading the source as if it were the "
                "result."
            )
    if undecodable_dollar_quote(command):
        return (
            "a `$'…'` carries an escape this guard does not decode, so it "
            "cannot tell what the word says; refusing rather than reading "
            "part of it."
        )
    return None


def undecodable_dollar_quote(command):
    """Whether a `$'…'` or `$"…"` in `command` carries an escape to decode.

    The same decision `undecodable_heredoc` records, one construct along, and
    for the same reason: decoding every escape bash supports is a list that
    trails bash's, and each gap in one reopens the hole it was written to
    close. `$'\\''` is the shape that forces the question — it is a single
    quote produced by an escape, which desynchronised `substitutions` and sent
    the whole command down the `ValueError` path, where the push allow-list
    does not run.

    **Both forms can fail, and the locale one always does.** `$'…'` can carry
    an escape outside the set `decode_ansi_c` knows, so it fails when it does.
    `$"…"` fails unconditionally, and the word *translated* is why.

    **The translation is a lookup in a catalogue this hook is not given**, and
    the first version of this paragraph named the wrong half of the problem. It
    said `$"…"` is a translated double-quoted string and then refused only the
    expansions inside it — as though `$"safe"` were the word `safe` once no
    substitution was present. It is not: bash resolves `$"…"` through gettext
    against `TEXTDOMAIN` and `TEXTDOMAINDIR`, both ordinary environment
    variables, so a catalogue placed in the checkout decides what the word
    says. Measured with a hand-built `.mo`: `$"safe"` printed `printf`, and in
    command position `$"safe" RAN` **executed** it. The same lookup can return
    `git`. Raised in review.

    So this is the residual `docs/harness-boundaries.md` names — text the shell
    is *told* rather than text it is given — arriving in a construct a caller
    can type literally, and the answer is the one that file already states for
    a script on disk: what cannot be read is not judged, and what is not judged
    is refused. The cost is every `$"…"`, which nothing in this repository
    writes.
    """
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return True
        if decode_ansi_c(command[start + 2:end - 1]) is None:
            return True
    return False


def strip_dollar_quotes(command):
    """`command` with every `$'…'` and `$"…"` replaced by what it names.

    `shlex` has no rule for either form, so the `$` stayed glued outside the
    quote and `$'git'` tokenised as `$git` — which `program_name` did not match,
    so `git_segments` yielded nothing and the push allow-list, the forbidden
    flags and `ext::` were all skipped at once.

    The escapes are decoded rather than dropped, so `$'\\x67it'` becomes `git`
    and is judged as one. A body this file cannot read is refused before this
    runs — which is every locale-quoted one, and an ANSI-C one carrying an
    escape outside the decoded set — so neither the `None` case nor the
    translated form can arrive here.
    """
    out, cursor = [], 0
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            continue
        text = decode_ansi_c(command[start + 2:end - 1])
        if text is None:
            continue
        out.append(command[cursor:start])
        out.append(single_quoted(text))
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def join_continuations(command, quotes=True):
    """`command` with every line continuation removed, as bash removes them.

    **A backslash-newline is deleted before the shell tokenises anything**, so
    `git 2\\<newline>>&1 push origin +HEAD:main` reaches git as
    `git push origin +HEAD:main` with `2>&1` applied — and the guard, reading
    the backslash as an ordinary escape, stopped the descriptor scan at it,
    stripped `>&1` alone and left `2` sitting where the subcommand goes. The
    bare form `git \\<newline>push origin +HEAD:main` did the same thing with
    no descriptor at all. Both raised in review, both verified allowed, and
    both allowed on `main` before this file had a redirection strip.

    `separate_lines` deliberately keeps the pair — a continuation is not a
    separator — and that is still true; what was missing is that it is not an
    argument either. It is removed here, before anything reads a word, which is
    the order bash uses.

    **Inside single quotes a backslash is literal**, so a continuation there is
    two ordinary characters and stays. Inside double quotes bash removes it,
    and so does this.

    **`quotes` is false for a heredoc BODY, where a quote is an ordinary
    character and the continuation goes anyway.** An expanding body removes
    `\\<newline>` before it expands, so
    `git commit -F - <<EOF` / `$\\<newline>(git push origin +HEAD:main)` / `EOF`
    forms a live `$(…)` and runs the push — while this function, tracking
    quotes that are not quotes, could reach the wrong conclusion about where
    the escape sits. Raised in review; verified allowed.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command, quotes=quotes)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            if command[index + 1] == "\n":
                index += 2
                continue
            # Any other escape is passed through whole, so an escaped quote
            # never toggles the state below.
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        out.append(char)
        index += 1
    return "".join(out)


def separate_lines(command):
    """`command` with every unquoted newline turned into a `;`.

    **A newline separates commands, and `shlex` made it disappear.** With
    `whitespace_split=True` a newline is whitespace: it is never emitted as a
    token, so the `"\n"` in `SEPARATORS` matched nothing and every line of a
    script joined the run before it. Harmless while a `git` token anywhere was
    an invocation — and a bypass the moment `DATA_ONLY_COMMANDS` arrived, since

        echo hi
        git push origin +HEAD:main

    became one `echo`-led run and the push was exempt. Found while fixing a
    narrower case from review; the reported input was a comment inside a
    substitution, and this is why closing that one was not enough.

    A newline inside quotes is data and stays — `git commit -m "a<newline>b"`
    is one argument. So is one after a backslash, which is a line continuation
    bash removes rather than a separator.
    """
    out, escaped = [], None
    for index, in_quotes, in_comment in shell_positions(command):
        char = command[index]
        if index == escaped:
            out.append(char)
            escaped = None
            continue
        if char == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            out.append(char)
            continue
        if char == "\n" and not in_quotes and not in_comment:
            out.append(";")
        else:
            out.append(char)
    return "".join(out)


# Redirection operators, longest first so that `>>` is never read as a `>`
# with a stray `>` behind it. **`<<` and `<<<` are absent from this tuple
# because they are matched before it**, each by a branch of its own:
# `redirection_spans` argues both.
REDIRECTION_OPERATORS = ("&>>", "&>", ">>", ">&", ">|", "<>", "<&", ">", "<")

# **The operators that OPEN THE TARGET FOR WRITING**, which is the half #20 is
# about. `<`, `<<` and `<<<` read, so a protected path on the right of one is a
# read this hook has no quarrel with. `<>` is here because it opens read-write.
#
# `>&` and `<&` are the fd-duplication forms and are judged by their target
# rather than by the operator: bash reads `>&1` and `>&-` as duplication and
# `>&file` as `&>file`, redirecting both streams into a file. So the operator
# alone cannot say, and `writes_to_a_file` asks about the word.
WRITING_OPERATORS = frozenset({"&>>", "&>", ">>", ">|", "<>", ">"})
DUPLICATING_OPERATORS = frozenset({">&", "<&"})

# What `redirection_spans` found: the span bash consumes, the operator it read,
# and the source text of the target word. `target` is the empty string for a
# heredoc introducer, whose delimiter is a name rather than a path.
Redirection = collections.namedtuple(
    "Redirection", "start end operator target")


def word_end(command, position, ordinary):
    """The end of the shell WORD beginning at `position`.

    **One parse of a word, because there were two and they disagreed.**
    `stdin_scripts` had its own, ending a here-string at the first
    unquoted metacharacter — so `bash <<<$(printf 'git push origin
    +HEAD:main')` yielded `$` as the script, the inner `printf` was judged
    as the data it is, and the redirection strip removed the rest. The push
    ran and the hook admitted it. Raised in review; verified allowed, with
    the backtick spelling beside it — which is this parse's OWN fail-open,
    recorded below, arriving a second time in the function that did not
    share it.

    **A substitution is part of the word, and stopping at its `(` was a
    fail-open.** A word ends at an unquoted metacharacter — but the `(` of
    `$(…)` is not one to bash, it opens a nested command list. Stopping
    there left the parentheses standing, `is_boundary` read them as run
    boundaries, and `git >/tmp/$(echo x) push origin +HEAD:main` had its
    `git` severed from its own subcommand: the force push ran and the guard
    admitted it. Raised in review; verified allowed, with `$((…))`, a bare
    `$(…)` target and a backtick spelling beside it.

    An UNBALANCED opener stops the word instead of swallowing the rest of
    the line, because consuming to the end would hide whatever followed —
    the same fail-open one layer along.

    **And a word may not BEGIN with `(`, which is the difference between a
    substitution inside a target and a process substitution being one.**
    `echo <(git push origin +HEAD:main)` is not a redirect with `(…)` for a
    target: `<(` is one construct, the inner command runs, and consuming it
    as a word deleted that push from the judged string outright. Caught by
    `test_a_process_substitution_is_not_the_printers_argument`, which is
    why it exists — the same reading applies to `> >(tee f)`, whose target
    is a process substitution that also runs. Left alone, the parentheses
    stay the run boundaries they already were and the inner command is
    judged in its own right.
    """
    def plain(offset):
        return offset < len(command) and ordinary[offset]

    first = position
    while position < len(command):
        char = command[position]
        if not ordinary[position]:
            position += 1
            continue
        if char == "`":
            # **Escape-aware, because `\`` is how the legacy form nests.**
            # A plain `find` ended the word at the inner delimiter of
            # `` >/tmp/`echo \`echo x\`` `` and left the outer backtick
            # sitting where the subcommand goes. `substitutions` already
            # scans this way; the two agree on purpose.
            scan = position + 1
            while scan < len(command):
                if command[scan] == "\\" and scan + 1 < len(command):
                    scan += 2
                    continue
                if command[scan] == "`":
                    break
                scan += 1
            if scan >= len(command):
                return position
            position = scan + 1
            continue
        if command.startswith("${", position):
            # **A parameter expansion is part of the word, metacharacters
            # and all.** `>${PATH:+/tmp/x;y}` redirects to `/tmp/x;y`, and
            # returning at that `;` left a separator standing between `git`
            # and its subcommand.
            close = _closing_brace(command, position + 2)
            if close is None:
                return position
            position = close + 1
            continue
        if char == "(":
            if position == first:
                return position
            close = _closing_paren(command, position + 1)
            if close is None:
                return position
            position = close + 1
            continue
        if char in METACHARACTERS:
            return position
        position += 1
    return position


def ordinary_positions(command):
    """Whether each character of `command` is unquoted, unescaped shell text.

    One mask for the two walks that split a command into words with their
    quoting intact — `redirection_spans` and `raw_runs` — so that they cannot
    disagree about which characters are syntax.
    """
    ordinary = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(command):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment
    return ordinary


def redirection_spans(command):
    """Every redirection in `command`, as `Redirection` records.

    `start` is the first character of the file descriptor where one is written
    and of the operator otherwise, and `end` is just past the target word — so
    `command[start:end]` is everything bash consumes as redirection syntax and
    never hands to the program.

    **The operator and the target travel with the span because there were very
    nearly two parses of this grammar.** `strip_redirections` wants only the
    span; #20 wants the target, and a second walk to find it would be the
    disagreement `word_end`'s own docstring records happening again one
    function along. One parse, three consumers.

    **A heredoc introducer IS one of these, and an earlier revision of this
    docstring said the opposite.** The reasoning then was that `strip_heredocs`
    leaves the introducer standing so the line still tokenises, and that
    removing `<<` would strand its delimiter as a stray word. The second half
    was true and the conclusion did not follow: `<<` is whole punctuation, so
    leaving it made it a run boundary and severed `git` from its own
    subcommand — a fail-open. The introducer goes **with** its delimiter, which
    strands nothing, and `HEREDOC` is the one parse of that grammar this file
    has. A here-string is matched before either, since `<<<` has `<<` as a
    prefix.

    Raised in review, and the paragraph is kept in this shape deliberately: a
    docstring that still argued for the old behaviour is how the next edit
    restores it.
    """
    ordinary = ordinary_positions(command)

    def plain(position):
        return position < len(command) and ordinary[position]

    spans, index = [], 0
    while index < len(command):
        if not ordinary[index]:
            index += 1
            continue
        start = index
        digits = index
        while plain(digits) and command[digits].isdigit():
            digits += 1
        if digits == start and command[start] == "{":
            # **The descriptor grammar is not only digits**, and reading it as
            # digits alone left `git {fd}>&1 push origin +HEAD:main` admitted
            # while bash ran the force push: `>&1` went, `{fd}` stayed, and
            # `push_offence` took that word for the subcommand and stopped
            # looking. Raised in review on the change that closed the digit
            # half; verified allowed before the fix. Bash takes `{name}` where
            # name is an identifier, so a leading digit is not one.
            close = start + 1
            if plain(close) and (command[close].isalpha() or command[close] == "_"):
                while plain(close) and (command[close].isalnum()
                                        or command[close] == "_"):
                    close += 1
                if plain(close) and command[close] == "}":
                    digits = close + 1
        begins_word = start == 0 or (
            ordinary[start - 1] and command[start - 1] in METACHARACTERS)
        if digits > start and not begins_word:
            # **A descriptor is a WHOLE token glued to the operator**, which is
            # bash's own rule rather than an approximation of it: in
            # `echo foo2>x` the word bash writes is `foo2` and only `>x` is
            # syntax. Reading the digits here would be editing an argument,
            # which is the thing `shell_positions` exists to stop this file
            # doing.
            index = digits
            continue
        if command[digits:digits + 3] == "<<<":
            # A here-string's word is data the shell feeds in, exactly like a
            # redirect target — and it is checked before `<<`, which is a
            # prefix of it. Left to the branch below, `<<<x` was reduced to a
            # bare `<<` that still split the run.
            end = digits + 3
            while plain(end) and command[end] in " \t":
                end += 1
            target = end
            end = word_end(command, end, ordinary)
            spans.append(Redirection(start, end, "<<<", command[target:end]))
            index = end
            continue
        if command[digits:digits + 2] == "<<":
            # **A heredoc introducer goes WITH its delimiter, and leaving it
            # standing was a fail-open.** `strip_heredocs` takes the body and
            # leaves this behind so the rest of the line still tokenises — but
            # `<<` is whole punctuation, so `is_boundary` ends the run there:
            # in `git <<EOF push origin +HEAD:main` the `git` token was severed
            # from its own subcommand, `git_segments` yielded nothing, and bash
            # ran the force push. Raised in review; verified allowed, and
            # allowed on `main` before this file grew a strip at all.
            #
            # Removing the delimiter with it is what leaves no stray word, and
            # `HEREDOC` is the one parse of that grammar this file has — the
            # dash form and both quoted spellings included.
            introducer = HEREDOC.match(command, digits)
            if introducer is not None:
                spans.append(
                    Redirection(start, introducer.end(), "<<", ""))
                index = introducer.end()
                continue
            # An introducer this file cannot parse keeps its old treatment, and
            # a descriptor in front of one is still the stray word every other
            # spelling leaves.
            if digits > start:
                spans.append(Redirection(start, digits, "<<", ""))
            index = digits + 2
            continue
        operator = None
        for candidate in REDIRECTION_OPERATORS:
            reach = range(digits, digits + len(candidate))
            if command[digits:digits + len(candidate)] == candidate and all(
                    plain(position) for position in reach):
                operator = candidate
                break
        if operator is None:
            index = digits + 1 if digits == start else digits
            continue
        end = digits + len(operator)
        while plain(end) and command[end] in " \t":
            end += 1
        # **A process substitution can BE the target, and leaving it to the run
        # splitter hides the outer command.** In
        # `git > >(tee /tmp/log) push origin +HEAD:main` both `>` characters
        # were removed separately and `(tee /tmp/log)` stayed as a boundary
        # between `git` and `push` — bash runs the force push and the guard
        # admitted it. Raised in review, twice: the round before this one
        # asserted in a comment that the run splitter covered this case, which
        # was true of the INNER command and false of the outer one.
        #
        # So it is consumed as the word it is, and `substitutions` grew the
        # same construct in the same change — a target nothing judged would be
        # the hole this one closes, one layer along.
        if (command[end:end + 2] in (">(", "<(")
                and plain(end) and plain(end + 1)):
            close = _closing_paren(command, end + 2)
            if close is not None:
                spans.append(Redirection(
                    start, close + 1, operator, command[end:close + 1]))
                index = close + 1
                continue
        target = end
        end = word_end(command, end, ordinary)
        spans.append(Redirection(start, end, operator, command[target:end]))
        index = end
    return spans


def strip_redirections(command):
    """`command` with every redirection removed, target word included.

    **A redirection is shell syntax and the file descriptor in front of one is
    not — to `shlex`.** `punctuation_chars=True` emits a maximal run of
    `();<>|&` as ONE token, so `>&` arrives whole, but a digit is not
    punctuation: the `2` of `2>&1` detaches and survives as an ordinary word.
    That one stray word reached every check downstream that counts non-flags,
    in three separate directions (#183):

        git push -u origin feat 2>&1        three positionals where two are
                                            required, so an honest push was
                                            refused for naming two refspecs
        git push -u origin 2>&1 +HEAD:main  `2` taken for the refspec — it
                                            satisfies `SAFE_REF` — while the
                                            real one fell past the `>&`
                                            boundary into a run of its own: a
                                            FORCE PUSH TO MAIN, admitted
        git 2>&1 log --output=/tmp/probe    the run split at `>&`, the second
                                            run led with `1` and held no `git`
                                            token, so #30's write primitive was
                                            admitted

    All measured against the guard as shipped. Removing the whole redirection
    is what makes the remaining string the argv bash passes to the program,
    which is the one thing this hook claims to judge — and it is one strip in
    the pipeline both paths read rather than a relaxed count in whichever check
    someone happened to be looking at.
    """
    out, cursor = [], 0
    for span in redirection_spans(command):
        out.append(command[cursor:span.start])
        cursor = span.end
    out.append(command[cursor:])
    return "".join(out)


def writes_to_a_file(span):
    """Whether this redirection opens its target for writing.

    The duplication operators are the only ones the operator cannot answer for.
    Bash reads `>&1` and `>&-` as duplication of a descriptor, and `>&word` as
    `&>word` — both streams into a FILE — so the word decides. A descriptor is
    digits or `-`; anything else is a path.
    """
    if span.operator in WRITING_OPERATORS:
        return True
    if span.operator not in DUPLICATING_OPERATORS:
        return False
    word = span.target.strip()
    return bool(word) and word != "-" and not word.isdigit()


def globbed(word):
    """Whether `word` carries an unquoted pathname-expansion metacharacter.

    **Bash expands a redirection target, and the expansion is what opens the
    file.** `ls > package.jso?` is expanded to the existing `package.json`
    before the redirect is performed, while `shlex` hands back the literal
    pattern — so `protected_path` compared a string that is not the file, and
    the write landed on a name it would otherwise have refused. Raised by
    Copilot against the commit that added the check.

    Quoting is what turns it off, so quoting is what this asks about: `>
    "package.jso?"` names a file with a question mark in it and expands to
    nothing. `shell_positions` is the module's one answer to that question, and
    an escaped metacharacter is counted as unquoted here — over-refusal in a
    position where nothing legitimate writes.

    **`extglob` adds three openers that are not in that set**: `@(`, `+(` and
    `!(`. A shell started with `-O extglob` expands `package.@(json)` to the
    existing `package.json` exactly as it expands `package.jso?`, so each of
    them, unquoted and followed by `(`, counts as a pattern too. (`?(` and `*(`
    were already caught by their first character.) Raised by Copilot.
    """
    for index, in_quotes, in_comment in shell_positions(word):
        if in_quotes or in_comment:
            continue
        if word[index] in "*?[":
            return True
        if word[index] in "@+!" and word[index + 1:index + 2] == "(":
            return True
        # **A brace expansion is the same answer by another route.**
        # `package.{j..j}son` is a range bash expands to `package.json` before
        # the redirect opens it, and the literal holds no protected name.
        # Every unquoted `{` counts, rather than the forms that expand, because
        # enumerating those is how the range was missed. Raised by Copilot.
        if word[index] == "{" and word[index - 1:index] != "$":
            return True
    return False


def target_literal(word):
    """The filename `word` names, or `None` when that cannot be read.

    **A target built by a command substitution is refused rather than guessed
    at**, which is the answer this file gives everywhere else the deciding text
    is not in the source — `substitution_fed_shells`, `unmodelled_printer` and
    the stdin-script scan all say it in their own words. `substitutions` is
    asked rather than a `$(` scan written here, so single quotes still mean
    what they mean.

    A process substitution is the same answer for a nearer reason: `> >(sh -c
    …)` writes through a command rather than to a path, so there is no
    filename to judge.

    **A parameter expansion is left in the literal here and judged by the
    caller**, through `expanded_target`: this function answers what the word
    spells, and whether a `$F` in it can be read is a question about the
    variable rather than about the quoting.
    """
    if word.startswith(("<(", ">(")) or substitutions(word):
        return None
    try:
        parsed = shlex.split(strip_dollar_quotes(word), posix=True)
    except ValueError:
        return None
    return parsed[0] if parsed else None


def protected_path(literal):
    """Which protected surface `literal` names, or `None`.

    Judged on the components of the path as written. `docs/../.claude/x` holds
    a `.claude` component and is refused; so is `/tmp/checkout/.git/config`,
    which is the point of matching a component rather than a prefix.

    **Compared folded, on every host, because the filesystem decides and not
    the string.** Windows and a default macOS volume look names up without
    regard to case, so `ls > PACKAGE.JSON` and `ls > .CLAUDE/settings.json`
    overwrite the protected file while matching neither set as spelled. This
    hook cannot ask the volume — it has no `cwd` it can trust, which is the
    reason it judges lexically at all — so it folds everywhere, and a Linux
    redirect to a genuinely distinct `Package.json` is refused along with it.
    Raised by Copilot.

    Windows also discards trailing dots and spaces from a component, so
    `package.json.` opens `package.json`; those are stripped before comparing.
    An 8.3 short name — `PACKAG~1.JSO`, `CLAUDE~1` — is the same file under a
    spelling no set can list, so a `~<digit>` component is refused when its
    stem could abbreviate a protected name. Not every such component: Windows
    spells the temp root itself that way (`C:/Users/RUNNER~1/…`), and refusing
    those would take the session's scratch writes with it.

    **An NTFS stream suffix names the same file too.** `package.json::$DATA`
    opens the default data stream — the file's contents — while the component
    as written is not `package.json`. So a component is compared up to its
    first `:`. A drive letter is a component of its own (`C:`), which folds to
    `c` and names nothing here. Raised by Copilot.
    """
    parts = [part for part in re.split(r"[\\/]+", literal)
             if part not in ("", ".")]

    def comparable(part):
        return part.split(":", 1)[0].rstrip(". ").lower()

    for part in parts:
        short = re.match(r"([^~]+)~\d", part)
        if short and any(
                name.replace(".", "").startswith(short.group(1).lower())
                for name in PROTECTED_TREES_FOLDED | PROTECTED_FILES_FOLDED):
            return part
        if comparable(part) in PROTECTED_TREES_FOLDED:
            return part
    if parts and comparable(parts[-1]) in PROTECTED_FILES_FOLDED:
        return parts[-1]
    # `<project>.csproj.user` is imported by the SDK's common targets (#311): a
    # suffix rather than a name, because the project's name is the prefix.
    if parts and comparable(parts[-1]).endswith(".csproj.user"):
        return parts[-1]
    return None


# The variables a redirection target may expand, because this hook can read
# their values from its own environment, which the session shares.
EXPANDABLE_VARIABLES = frozenset({
    "CLAUDE_PROJECT_DIR", "HOME", "TEMP", "TMP", "TMPDIR",
})


def expanded_target(literal, command):
    """`literal` with its parameter expansions replaced, or `None`.

    **A parameter-expanded target was the stated residual, and it was a bypass
    on a globally approved command.** `F=.claude/settings.json; ls > $F` holds
    no protected component in the target as written, and bash opens the
    settings file. Raised by Copilot.

    Refusing every `$` would take the ordinary `> "$TMP/out"` scratch write
    with it, so the few variables whose values this hook shares with the
    session are expanded from its own environment, and judged as the path they
    produce. Everything else is refused: another name, a positional or special
    parameter, any `${…}` operator, an unset variable, and an allowed name that
    appears anywhere in the command other than as a plain reference — an
    assignment, a `read`, an `export`, a `for` loop — because then its value at
    the redirection is not the one this process holds.
    """
    references = re.compile(r"\$(?:\{([A-Za-z_][A-Za-z0-9_]*)\}|([A-Za-z_][A-Za-z0-9_]*))")
    failed = False

    def replace(match):
        nonlocal failed
        name = match.group(1) or match.group(2)
        value = os.environ.get(name)
        if name not in EXPANDABLE_VARIABLES or not value:
            failed = True
            return ""
        bare = references.sub("", command)
        if re.search(rf"(?<![A-Za-z0-9_]){name}(?![A-Za-z0-9_])", bare):
            failed = True
            return ""
        return value

    expanded = references.sub(replace, literal)
    if failed or "$" in expanded:
        return None
    return expanded


# **The application trees a command denies, judged only at a checkout's root.**
# `/review-branch` denies `Edit(src/**)`, `docs/**`, `e2e/**` and `public/**`,
# and a globally approved `ls > src/app/x.ts` wrote past every one of them —
# the list above named the machinery and nothing a read-only review exists not
# to touch. Raised by Copilot. A hook cannot see which command is running, so
# the union of every command's denies is what it protects.
#
# Matched as the FIRST component under the checkout containing the target,
# not at any depth like the trees above: `src` and `docs` are ordinary names,
# and refusing `/tmp/x/docs/out` would take scratch writes with them.
APPLICATION_TREES = frozenset({"docs", "e2e", "public", "src", "tests", "tools"})


def application_tree(literal):
    """Which application tree `literal` writes into at a checkout's root."""
    cwd = EVENT_CWD or os.getcwd()
    joined = literal if os.path.isabs(literal) else os.path.join(cwd, literal)
    lexical = os.path.normpath(os.path.abspath(joined))
    for path in (lexical, os.path.realpath(joined)):
        root = path
        while not os.path.exists(os.path.join(root, ".git")):
            parent = os.path.dirname(root)
            if parent == root:
                root = None
                break
            root = parent
        if root is None:
            continue
        try:
            relative = os.path.relpath(path, root)
        except ValueError:
            continue
        first = re.split(r"[\\/]+", relative)[0]
        if first.split(":", 1)[0].rstrip(". ").lower() in APPLICATION_TREES:
            return first
    return None


# A command word that moves the shell's working directory, wherever it stands
# in the command: after a separator, inside a group or a subshell, or behind
# a `builtin`/`command` wrapper, which the leading boundary also admits.
#
# `source`, `.` and `eval` are here too: a sourced script runs in the current
# shell and can `cd` in a file this hook never reads, and `eval` runs text it
# may build. Raised by Copilot.
#
# **`.` is the command only where a command stands** (#357): after a separator,
# a keyword or a wrapper, behind assignment words. As an operand it is a
# directory (`git add .`), and reading it as the dot-command refused every
# relative write beside it.
#
# **A wrapper that changes directory for the command it runs is one too** (#359):
# `env -C .claude cp x settings.json` and `env --chdir=.claude tee settings.json`
# land a relative target in `.claude` with no `cd` word in the command, and so
# does `find … -execdir cp x ./hook.py \;`, which runs from the directory of
# each match. `find`'s is a pattern below; `env`'s is `env_changes_directory`,
# which reads words rather than a regex (see there).
CHANGES_DIRECTORY = re.compile(
    r"(?:^|[\s;&|(){}`])(?:cd|pushd|popd|source|eval)(?=$|[\s;&|()])"
    r"|\s-(?:exec|ok)dir(?=$|[\s;&|()])"
    r"|(?:^|[;&|(){}`!\n]|\b(?:then|do|else|elif|if|while|until|time|builtin"
    r"|command|exec)\s(?:-\S*\s+)*)\s*(?:(?:\w+=(?:\"[^\"]*\"|'[^']*'|\S*)"
    r"|\d*[<>]+&?\s*(?:\"[^\"]*\"|'[^']*'|\S+))\s+)*\.(?=$|[\s;&|()])")


@functools.lru_cache(maxsize=32)
def changes_directory(command):
    """Whether `command` may change directory, read before AND after quotes.

    **Asked once per command, not once per target** (#359): the answer is a
    function of the text, `destination_offence` asks it for every redirect and
    operand, and the word scan below made a command of many redirects cost
    targets times length. The cache is on the string.

    **The raw text is not what bash runs.** `c''d .claude` spells no `cd` and
    bash runs `cd`, so the pattern is also asked of the command with every
    quote, `$` before a quote, and backslash removed — over-matching a `cd`
    inside a quoted string, which is the direction to be wrong in. An
    expansion that joins into `cd` is judged by the readings `offence` runs
    before this, each of which reaches here with the expansion gone. Raised
    by Copilot.
    """
    unquoted = re.sub(r"\$?[\"']|\\", "", command)
    # `env` is read three ways: as written (a Windows path keeps its
    # backslashes), without quotes, and without quotes or backslashes
    # (`en\v -C d` is `env -C d` to bash).
    return bool(CHANGES_DIRECTORY.search(command)
                or CHANGES_DIRECTORY.search(unquoted)
                or env_changes_directory(command)
                or env_changes_directory(re.sub(r"\$?[\"']", "", command))
                or env_changes_directory(unquoted))


def env_changes_directory(command):
    """Whether an `env` in `command` carries a directory option (#359).

    **Words, not a pattern, because a pattern had to choose between bounded and
    quadratic.** `env` may stand behind a path (`/usr/bin/env`, `env.exe`, any
    case, as Windows resolves it), take any number of `NAME=value` words and
    options before its own, and spell the option `-C dir`, `-Cdir`, in a
    cluster of env's own letters (`-iC`), or as an abbreviated `--chdir`.
    **`-S` and `--split-string` count too**: their argument is split into more
    words, and `env -S'-C .claude cp x settings.json'` hides the option inside
    one.

    **Neither where `env` stands nor where its words end is asked, because
    asking failed open in review.** A position check missed a newline, a
    redirect word, an assignment holding a space and every wrapper with
    arguments of its own (`sudo -u root env`, `timeout 5 env`), and splitting
    at separators missed one inside env's words (`env A=$(date) -C d`,
    `env 'A=;' -C d`). So a piece of the word must BE `env` (split at a path
    separator or a glued one, so `.env` and `printenv` are not), and the option
    is looked for in every later word of the command. That over-matches an `-S`
    or `-C` of the program env runs (`env X=1 ls -S`), `env -uC` (unset a
    variable named `C`), `echo env -C`, and a directory component named env
    (`grep foo app/env/x -C3`), which is the direction to be wrong in. Only the
    FIRST such `env` is scanned, since its scan covers every later one, which
    keeps this linear.
    """
    words = command.split()
    for index, word in enumerate(words):
        # Every piece of the word, so `env>/dev/null` and `/usr/bin/env` are
        # env and `.env` and `printenv` are not.
        if not any(piece.lower() in ("env", "env.exe")
                   for piece in re.split(r"[\\/:(){}`;&|<>]", word)):
            continue
        for later in words[index + 1:]:
            name = later.split("=", 1)[0]
            if re.fullmatch(r"-[iv0uaPCS]*[CS].*", later) or (
                    len(name) > 2 and name.startswith("--")
                    and ("--chdir".startswith(name)
                         or "--split-string".startswith(name))):
                return True
        return False
    return False


# The directory the session's command runs in, from the hook event. `None`
# until `decision` reads one, and then the process's own directory stands in.
# `decision` sets it on every event it judges, so a caller that judges many
# events in one process cannot carry one command's directory into the next.
EVENT_CWD = None


def linked_protected_path(literal):
    """Which protected surface `literal` reaches THROUGH a link, or `None`.

    **The lexical check reads the spelling, and bash opens the file.** A branch
    can carry `docs/out -> ../.claude/settings.json`, and `ls > docs/out`
    holds no protected component while the write lands on the denied file.
    Raised by Copilot.

    So the target is placed against the checkout that contains it, resolved
    through the filesystem — a missing leaf beneath a linked parent included,
    which `realpath` resolves as far as the path exists — and judged again
    WHERE IT LANDS, but only where the two disagree. Comparing the resolution
    to the root's own resolution plus the spelled remainder is what keeps a
    checkout under a linked temp root (`/tmp` on macOS) from reading as a link
    on every write. A target outside every checkout is left to the lexical
    check: this list is the repository's machinery, not the filesystem's.
    """
    cwd = EVENT_CWD or os.getcwd()
    joined = literal if os.path.isabs(literal) else os.path.join(cwd, literal)
    lexical = os.path.normpath(os.path.abspath(joined))
    root = lexical
    while not os.path.exists(os.path.join(root, ".git")):
        parent = os.path.dirname(root)
        if parent == root:
            return None
        root = parent
    real_root = os.path.realpath(root)
    expected = os.path.normpath(
        os.path.join(real_root, os.path.relpath(lexical, root)))
    resolved = os.path.realpath(joined)
    if os.path.normcase(expected) == os.path.normcase(resolved):
        return None
    try:
        landed = os.path.relpath(resolved, real_root)
    except ValueError:
        landed = resolved
    if landed == ".." or landed.startswith(".." + os.sep):
        landed = resolved
    return protected_path(landed)


def cwd_protected_path(literal):
    """Which protected surface a RELATIVE `literal` lands in from the event's cwd.

    **The spelling carries no protected component when the session already
    stands inside the tree** (#361). One call runs `cd .claude/hooks`, which
    writes nothing; the next arrives with that directory as its `cwd`, and
    `cp x evil.sh` holds no `.claude` component while bash writes
    `.claude/hooks/evil.sh`. So a relative target is placed against the event's
    `cwd`, and the path from the checkout that contains it is judged as a
    spelling. An absolute target is left to the lexical check, and so is one
    that lands outside every checkout: this list is the repository's
    machinery, not the filesystem's.
    """
    if os.path.isabs(literal) or literal.startswith(("/", "\\")):
        return None
    if not EVENT_CWD:
        return None
    joined = os.path.join(EVENT_CWD, literal)
    # Both readings, as `application_tree` takes them. `linked_protected_path`
    # resolves a link too, but it walks up from the LEXICAL path to find the
    # checkout, so a cwd that is a link living outside the checkout (`/tmp/l`
    # into `.claude/hooks`) finds no root there and is left to this one.
    for path in (os.path.normpath(os.path.abspath(joined)),
                 os.path.realpath(joined)):
        root = path
        while not os.path.exists(os.path.join(root, ".git")):
            parent = os.path.dirname(root)
            if parent == root:
                root = None
                break
            root = parent
        if root is None:
            continue
        try:
            relative = os.path.relpath(path, root)
        except ValueError:
            continue
        named = protected_path(relative)
        if named:
            return named
    return None


def redirection_offence(command):
    """The reason to refuse a redirection in `command`, or `None`.

    **This is the half of the deny lists that was defence in depth and is now a
    boundary.** Every `Edit(...)` entry in `.claude/settings.json` and in every
    command's `disallowed-tools` binds the editing tools; `Bash(ls:*)` and
    `Bash(wc:*)` are auto-approved for every session, and a `>` on either one
    wrote what all of them refuse.

    **The residual this docstring used to state was false, and it is kept here
    in the past tense for that reason.** It said a redirection is not the only
    way a command writes — `tee`, `cp`, `sed -i` and an interpreter all do —
    and that those did not need judging because each "has to be granted
    first". Under `"defaultMode": "auto"`, set in a user-level settings file
    this repository never reads, nothing has to be granted: a `cp` onto the
    `src` a redirection here had just refused was admitted and landed (#26).
    `writing_verb_offence` judges the verbs now; what stays open is named
    there.
    """
    for span in redirection_spans(command):
        if not writes_to_a_file(span):
            continue
        # **An empty target is not a write and refusing it broke an admitted
        # case.** `word_end` stops without consuming anything when the word
        # would BEGIN with `(`, because `git log >(cat) -1` is a process
        # substitution bash passes as an ARGUMENT rather than a redirect with
        # `(cat)` for a target — so the span holds a bare `>` and there is no
        # path here to judge. What runs inside those parentheses is judged by
        # the run splitter, which is the arrangement
        # `test_a_substitution_is_part_of_the_target_word` pins.
        if not span.target.strip():
            continue
        refusal = destination_offence(
            span.target, command, f"`{span.operator}`", "a redirection")
        if refusal is not None:
            return refusal
    return None


def destination_offence(word, command, writer, kind):
    """The reason to refuse writing the file shell `word` names, or `None`.

    `word` is raw shell text, quoting intact, because the quoting is what
    decides whether a `~`, a `*` or a `$"…"` means anything. `writer` names the
    operator or the program in a refusal, and `kind` names the way it writes.

    **One judgement, two callers, and the second is why it was lifted out.**
    It was `redirection_offence`'s loop body while a redirection was the only
    write this file judged. A `cp` writes the same file by another spelling
    (#26), and a second copy of these rules for it is the sibling-drift
    `quote_states` records this file repeating five times.
    """
    if globbed(word):
        return (
            f"{kind}'s target carries an unquoted `*`, `?`, `[` or `{{`, so "
            "bash expands it and the file written is not the string written "
            "here — `> package.jso?` writes `package.json`. Refusing rather "
            "than judging the pattern instead of the file: quote the name, or "
            "write it out (#20, #26, docs/harness-boundaries.md)."
        )
    # **A dollar quote this guard cannot read is refused on the target
    # itself**, because the command-wide check runs after the redirection
    # strip has already removed it. `target_literal` read `$"HOME"/../src/…`
    # as a `$HOME` expansion and judged `~/../src/…`, while bash opens the
    # relative `HOME/../src/…` — the checkout's denied `src`. Raised by
    # Copilot.
    unreadable = unreadable_dollar_quote(word)
    if unreadable is not None:
        return f"{kind}'s target: {unreadable}"
    literal = target_literal(word)
    if literal is None:
        return (
            f"{kind}'s target is built by a command substitution, so the file "
            "it writes cannot be read from this command; refusing rather than "
            "admitting a write nothing judged. Name the path, or use the "
            "editing tools, which the permission rules see (#20, #26, "
            "docs/harness-boundaries.md)."
        )
    # **An unquoted leading `~` is expanded before the file opens.**
    # `ls > ~/checkout/src/app/x.ts` reached the tree checks as a relative
    # path whose first component is `~`, while bash wrote the checkout's
    # `src`. Raised by Copilot. `~` and `~+` have values this hook shares
    # with the session — the home directory, and the working directory the
    # event names — so they are expanded and judged; `~-` and `~user`
    # have none it can read, and are refused. A quoted `'~'` is a literal
    # name, which is why the raw word is asked rather than the literal.
    raw = word.strip()
    if raw.startswith("~"):
        prefix = re.match(r"~[^/\\]*", raw).group(0)
        remainder = literal[len(prefix):]
        if prefix == "~":
            literal = os.environ.get("HOME") or os.path.expanduser("~")
            literal += remainder
        elif prefix == "~+":
            literal = (EVENT_CWD or os.getcwd()) + remainder
        else:
            return (
                f"{kind}'s target begins `{prefix}`, which bash expands to a "
                "directory this guard cannot read — the previous working "
                "directory, or another user's home. Refusing rather than "
                "judging the tilde instead of the path (#20, #26, "
                "docs/harness-boundaries.md)."
            )
    if "$" in literal:
        expanded = expanded_target(literal, command)
        if expanded is None:
            return (
                f"{writer} writes `{literal}`, whose path is built by a "
                "parameter expansion this guard cannot read — a variable set "
                "earlier in the same command, or one outside "
                f"{', '.join(sorted(EXPANDABLE_VARIABLES))}. Refusing rather "
                "than judging the name instead of the file it opens (#20, "
                "#26, docs/harness-boundaries.md)."
            )
        # **An expanded value can be a pattern too**, and `globbed` ran on
        # the word before it was expanded. With `TMPDIR=package.jso?`,
        # `ls > $TMPDIR` opens `package.json` while the tree checks see
        # the pattern. Refused whether or not the expansion was quoted: a
        # temp root carrying `*`, `?`, `[` or `{` is not one to write
        # scratch into. Raised by Copilot.
        if any(char in expanded for char in "*?[{"):
            return (
                f"{kind}'s target `{literal}` expands to `{expanded}`, which "
                "carries a pattern character, so the file bash opens is not "
                "the string judged here (#20, #26, docs/harness-boundaries.md)."
            )
        literal = expanded
    # **A relative target is placed against the event's `cwd`, and a
    # directory change earlier in the command moves where it lands.**
    # `ls >/dev/null; cd .claude; ls > settings.json` was judged as
    # `<checkout>/settings.json` and bash wrote `.claude/settings.json`.
    # Modelling `cd`, `pushd` and `popd` through subshells and compound
    # commands is the kind of shell emulation this file refuses to guess
    # at, so a relative write target is refused whenever the command can
    # change directory. Name the path absolutely, or split the command.
    # Raised by Copilot.
    # A leading slash is absolute to bash on every host, and to
    # `os.path.isabs` only where there is no drive letter to ask for.
    absolute = os.path.isabs(literal) or literal.startswith(("/", "\\"))
    if not absolute and changes_directory(command):
        return (
            f"{writer} writes the relative path `{literal}` in a command that "
            "also changes directory, so where it lands is not the event's "
            "working directory and this guard cannot place it. Name the path "
            "absolutely, or run the `cd` as its own command (#20, #26, "
            "docs/harness-boundaries.md)."
        )
    named = (protected_path(literal) or linked_protected_path(literal)
             or cwd_protected_path(literal) or application_tree(literal))
    if named is not None:
        return (
            f"{writer} would write `{literal}`, and `{named}` is the agent's "
            "own machinery or the toolchain that runs on it. Every `Edit(...)` "
            f"deny that names it binds the editing tools, so {kind} wrote "
            "straight past them (#20, #26, docs/harness-boundaries.md). Write "
            "it with an editing tool, where a permission rule judges the path."
        )
    return None


# **The programs whose file operands are written, and which of them are.**
# #26 measured the gap: a redirection onto `src/…` was refused, and the same
# bytes written to scratch and then `cp`'d onto the same path were admitted and
# landed. The residual this file stated for it — that `cp`, `tee` and the rest
# "have to be granted first" — assumed a permission mode no file here sets; a
# user-level `"defaultMode": "auto"` approves an un-granted `cp` outright.
#
# The value is the operand model, and every model is one `verb_offence` reads:
#
#   destination  the last operand, or a `-t`/`--target-directory` value; the
#                other operands are sources and are only read
#   every        every operand, because the verb writes, replaces or removes
#                each one — `mv` removes its sources, which is a write to them
#   in-place     the file operands, but only under `-i` / `--in-place`;
#                `sed` also has its script read for `w`, `W` and `e`
#   of           the `of=` operand
#   find         the file an `-fprint*` or `-fls` action names, and the
#                starting points of `-delete`
#
# **The list trails the programs that write, and says so.** `curl -o`,
# `rsync`, `tar -x`, `unzip`, `patch` and an interpreter
# (`python -c`, `node -e`, an `awk` script using its own `print >`) all write
# and none is here. The suite asserts every name below reaches the check
# through its model, and that the verbs #26 named are all on it — it cannot
# assert that nothing is missing, and `docs/harness-boundaries.md` carries that
# residual.
WRITING_VERBS = {
    "cp": "destination", "install": "destination", "ln": "destination",
    "mv": "every", "rm": "every", "rmdir": "every", "shred": "every",
    "tee": "every", "touch": "every", "truncate": "every", "unlink": "every",
    "perl": "in-place", "sed": "in-place",
    "dd": "of", "find": "find",
}
# `git restore`, `git checkout [<tree-ish>] -- PATH` and `git clean` write or
# delete the paths they are given too. `git_path_writer_offence` reads the
# subcommand and its operands: a pathspec on a protected surface is refused, a
# branch checkout names no path, and `git checkout HEAD -- path` puts back the
# committed copy and is admitted (#297).

# The verbs a `-t DIR` or `--target-directory=DIR` sends every operand into.
TARGET_OPTION_VERBS = frozenset({"cp", "install", "ln", "mv"})

# Short options that take a value, per verb — the value is the rest of the
# word, or the next word when nothing is glued. **Absent a letter, nothing is
# skipped**, which is the direction `VALUE_FLAGS_BY_SUBCOMMAND` argues: a value
# judged as a path costs an over-refusal on a name that is never protected,
# and a path skipped as a value costs the write.
SHORT_VALUE_OPTIONS = {
    "cp": "S", "install": "gmoS", "ln": "S", "mv": "S", "sed": "l",
    "shred": "ns", "touch": "dr", "truncate": "rs",
}

# The long forms, matched as GNU matches them: any unambiguous prefix.
LONG_VALUE_OPTIONS = {
    "cp": ("--suffix",), "install": ("--group", "--mode", "--owner",
                                     "--strip-program", "--suffix"),
    "ln": ("--suffix",), "mv": ("--suffix",),
    "sed": ("--expression", "--file", "--line-length"),
    "shred": ("--iterations", "--random-source", "--size"),
    "touch": ("--date", "--reference", "--time"),
    "truncate": ("--reference", "--size"),
}

# `find -exec cp {} dest \;` hands `cp` its words up to the terminator, and
# `{}` is a name `find` supplies — so the terminator ends the verb's operands.
FIND_EXECUTORS = frozenset({"-exec", "-execdir", "-ok", "-okdir"})

# Reserved words that can stand before a command in the same run.
SHELL_KEYWORDS = frozenset({
    "!", "{", "}", "do", "done", "elif", "else", "fi", "if", "then", "time",
    "until", "while",
})


def raw_runs(command):
    """`command` split into command runs of raw words, quoting intact.

    The token path cannot be asked this: `shlex` has already removed the quotes
    that decide whether a `~`, a `*` or a `$` in a destination expands, which is
    exactly what `destination_offence` has to read. `word_end` is the one parse
    of a word, and a process substitution is kept whole as one — splitting at
    its parenthesis would put `cp <(…) .claude/x`'s destination in a run of its
    own, led by the path.
    """
    ordinary = ordinary_positions(command)
    runs, current, index = [], [], 0
    while index < len(command):
        if ordinary[index] and command[index] in " \t":
            index += 1
            continue
        # **The redirection strip has already taken the `<` of a `<(…)`**,
        # reading it as an operator with an empty target, so what reaches here
        # from `cp <(echo x) .claude/settings.json` is `cp (echo x) …`. A group
        # standing where an argument stands is therefore one word, and its body
        # is split as runs of its own as well, so `if (cp a b)` loses nothing.
        opener = (2 if command[index:index + 2] in ("<(", ">(")
                  else 1 if command[index] == "(" and current else 0)
        if ordinary[index] and opener:
            close = _closing_paren(command, index + opener)
            if close is not None:
                current.append(command[index:close + 1])
                runs.extend(raw_runs(command[index + opener:close]))
                index = close + 1
                continue
        end = word_end(command, index, ordinary)
        if end == index:
            if current:
                runs.append(current)
            current = []
            index += 1
            continue
        current.append(command[index:end])
        index = end
    if current:
        runs.append(current)
    return runs


def _glued_raw(raw, literal, cut):
    """The raw text of `literal[cut:]`, quoted when `raw` spells it otherwise.

    A value glued to an option — `-t.claude`, `--target-directory=src` — is a
    word of its own to the verb, and its quoting still decides what expands. It
    is taken from the raw word when the option in front is spelled plainly, and
    single-quoted otherwise, since then the whole word was quoted and nothing
    in the value expands either.
    """
    if raw.startswith(literal[:cut]):
        return raw[cut:]
    return shlex.quote(literal[cut:])


def _unreadable_word(literal, command):
    """Whether a word's value is not in the source: a substitution, or an
    expansion `expanded_target` cannot read."""
    return literal is None or (
        "$" in literal and expanded_target(literal, command) is None)


# `find` writes through four actions and removes through one. The file an
# `-fprint`, `-fprint0`, `-fprintf` or `-fls` action names is created or
# truncated, and `-delete` removes everything the starting points reach (#297).
FIND_WRITING_ACTIONS = frozenset({"-fprint", "-fprint0", "-fprintf", "-fls"})


def brace_group_expands(word, start):
    """Whether the unquoted `{` at `start` opens a brace expansion.

    It does when an unquoted comma or `..` stands at the group's own depth
    before its unquoted `}`. A `}` inside quotes does not close it, so
    `-e{'w a}','w b'}` is two words. A group with no unquoted closer is
    literal to bash; one that still has a later `}` is refused anyway, since
    this walk is not bash (#297).
    """
    depth, quote, i, multiplies = 0, None, start, False
    while i < len(word):
        char = word[i]
        if quote == "'":
            if char == "'":
                quote = None
        elif char == "\\":
            i += 1
        elif char == "'" and quote is None:
            quote = "'"
        elif char == '"':
            quote = None if quote == '"' else '"'
        elif quote is None:
            if char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0:
                    return multiplies
            elif depth == 1 and (char == "," or word.startswith("..", i)):
                multiplies = True
        i += 1
    return "}" in word[start:]


def command_readings(command):
    """The command as written, with its quotes and backslashes gone, and with
    its parameter expansions removed as well: `"re"ad`, `GIT_WORK"_TREE"=x`
    and `r${E}ead` are `read` and an assignment to bash (#297)."""
    plain = re.sub(r"[\\'\"]", "", command)
    return (command, plain, re.sub(r"\$\{[^}]*\}|\$\w+", "", plain))


BASH_MANAGED_VARIABLES = frozenset({
    "REPLY", "MAPFILE", "OLDPWD", "PWD", "OPTARG", "OPTIND", "RANDOM",
    "SECONDS", "LINENO", "PIPESTATUS", "COMP_WORDS", "HISTCMD", "SHELLOPTS",
    "DIRSTACK", "FUNCNAME", "HISTFILE", "GROUPS", "PATH", "IFS",
})


def environment_variable(word, command):
    """Whether `word` is a lone `$NAME` or `${NAME}` that `command` never
    mentions except as that expansion, so nothing in the command can have set
    it (#297)."""
    match = re.fullmatch(r'"?\$(?:(\w+)|\{(\w+)\})"?', word.strip())
    if not match:
        return False
    name = match.group(1) or match.group(2)
    # Only a conventional environment name: bash manages `$_`, `$REPLY`,
    # `$OLDPWD`, `$MAPFILE` and the like, and sets them without naming them.
    if (not re.fullmatch(r"[A-Z][A-Z0-9_]*", name) or name.startswith("BASH")
            or name in BASH_MANAGED_VARIABLES or name in EXPANDABLE_VARIABLES):
        return False
    # Code built at run time can set a variable whose name never appears:
    # `eval "$(printf '\\x46=.claude/x')"; git checkout $F`. The `.` builtin
    # is a command word after a separator, a newline, a brace, a keyword or
    # `builtin`/`command`.
    dynamic = re.compile(
            r"\beval\b|\bsource\b|`|\$\(|<\(|\$'|\$\{!"
            r"|(?:^|[\s;&|(){!])\.(?=\s|$)"
            r"|\b(?:read|mapfile|readarray|getopts|cd|pushd|popd|declare|typeset"
            r"|export|readonly|local|let|set)\b|printf\s+-v", re.MULTILINE)
    # Read the command as written and with its quotes and backslashes gone:
    # `"re"ad` and `r\ead` are `read` to bash.
    if any(dynamic.search(text) for text in command_readings(command)):
        return False
    # A value the hook itself inherited is the value the shell inherited.
    inherited = os.environ.get(name)
    if inherited and (os.path.isabs(inherited)
                      or ".." in re.split(r"[\\/]", inherited)
                      or protected_path(inherited)
                      or linked_protected_path(inherited)
                      or application_tree(inherited)):
        return False
    # Only the bare forms are the expansion: `${F:=.claude/x}` assigns, and
    # leaves its name in the text for the search below to find.
    rest = re.sub(r"\$" + re.escape(name) + r"\b|\$\{" + re.escape(name) + r"\}",
                  "", command)
    return re.search(r"\b" + re.escape(name) + r"\b", rest) is None


def shell_expands(word):
    """Whether bash expands something in the raw `word`: a `$` or a backtick
    outside single quotes and not escaped, or an unquoted brace group that
    holds a comma or `..`, which multiplies one word into several
    (`-e{'w a','w b'}` is two scripts) (#297).

    A `$` expands only before a name, a digit, a brace, a parenthesis, a quote
    or a special parameter; `s/foo$/x/` and `{}` (find's placeholder) are
    literal to bash and are not flagged.
    """
    quote, i = None, 0
    while i < len(word):
        char = word[i]
        if quote == "'":
            if char == "'":
                quote = None
        elif char == "\\":
            i += 1
        elif char == "'" and quote is None:
            quote = "'"
        elif char == '"':
            quote = None if quote == '"' else '"'
        elif char == "`":
            return True
        elif char == "$" and (
                word[i + 1:i + 2].isalnum()
                or word[i + 1:i + 2] in ("_", "{", "(", "[", "@", "*", "#", "?",
                                         "!", "-", "$")
                # `$'…'` and `$"…"` are forms only outside quotes; inside
                # double quotes the quote after the `$` closes the string.
                or (quote is None and word[i + 1:i + 2] in ("'", '"'))):
            return True
        elif char == "{" and quote is None and brace_group_expands(word, i):
            return True
        i += 1
    return False


# The `sed` commands that take no argument, `{` and `}` among them.
SED_NO_ARGUMENT = frozenset("=dDgGhHnNpPxzF{}")


def sed_script_actions(script):
    """The writes and executions in a `sed` script, as `(command, text)`.

    `w` and `W` write the file the rest of the line names, `s///w` likewise, and
    `e` and `s///e` run a command; every other command is stepped over, so a `w`
    inside an address, a regular expression, a replacement or the text of `a`,
    `i` and `c` is not read as a command. A script this cannot read to its end
    raises `ValueError`, and the caller refuses it (#297).
    """
    actions, i, n = [], 0, len(script)

    def line_end(start, continued=False):
        # A comment or a file name ends at the newline, whatever precedes it
        # (GNU sed 4.9 measured); only the text of `a`, `i` and `c` is
        # continued by a backslash.
        j = start
        while j < n and script[j] != "\n":
            j += 2 if continued and script[j] == "\\" else 1
        return min(j, n)

    def delimited(start, delimiter):
        j = start
        while j < n and script[j] != delimiter:
            j += 2 if script[j] == "\\" else 1
        if j >= n:
            raise ValueError("an unterminated pattern")
        return j + 1

    def digits(j):
        while j < n and script[j].isdigit():
            j += 1
        return j

    def address(j):
        # The index after one address at `j`, or `None` when none starts there.
        if script[j:j + 1].isdigit():
            j = digits(j)
            if script[j:j + 1] == "~":
                j = digits(j + 1)
            return j
        if script[j:j + 1] == "$":
            return j + 1
        if script[j:j + 1] == "/":
            j = delimited(j + 1, "/")
        elif script[j:j + 1] == "\\" and j + 1 < n:
            j = delimited(j + 2, script[j + 1])
        else:
            return None
        while j < n and script[j] in "IM":
            j += 1
        return j

    while i < n:
        if script[i] in " \t\n;{}":
            i += 1
            continue
        first = address(i)
        if first is not None:
            i = first
            while i < n and script[i] in " \t":
                i += 1
            if script[i:i + 1] == ",":
                i += 1
                while i < n and script[i] in " \t":
                    i += 1
                if script[i:i + 1] in ("+", "~"):
                    i = digits(i + 1)
                else:
                    second = address(i)
                    if second is None:
                        raise ValueError("an address that does not end")
                    i = second
            while i < n and script[i] in " \t!":
                i += 1
            if i >= n:
                raise ValueError("an address with no command")
        command = script[i]
        i += 1
        if command in SED_NO_ARGUMENT:
            continue
        if command == "#":
            i = line_end(i)
        elif command in "lLqQ":
            while i < n and script[i] in " \t":
                i += 1
            i = digits(i)
        elif command in "btTv:":
            # A label ends at a blank as well as at `;`: `:x w out` writes
            # `out` (GNU sed 4.9 measured). An older sed reads further, and
            # reading less here only refuses more.
            while i < n and script[i] in " \t":
                i += 1
            while i < n and script[i] not in " \t;\n":
                i += 1
        elif command in "aic":
            i = line_end(i, True)
        elif command in "rR":
            i = line_end(i)
        elif command in "wWe":
            end = line_end(i)
            text = script[i:end].strip()
            if not text and command != "e":
                raise ValueError(f"a `{command}` with no file")
            actions.append((command, text))
            i = end
        elif command in "sy":
            if i >= n:
                raise ValueError("a command with no delimiter")
            delimiter = script[i]
            i = delimited(delimited(i + 1, delimiter), delimiter)
            while command == "s" and i < n:
                flag = script[i]
                if flag == "w":
                    end = line_end(i + 1)
                    text = script[i + 1:end].strip()
                    if not text:
                        raise ValueError("a `w` flag with no file")
                    actions.append(("w", text))
                    i = end
                    break
                if flag == "e":
                    actions.append(("e", ""))
                elif flag not in "gpiImME0123456789":
                    break
                i += 1
        else:
            raise ValueError(f"the command `{command}`")
    return actions


def sed_script_offence(scripts, script_file, command):
    """The reason to refuse the `sed` scripts given, or `None`.

    A `sed` without `-i` writes nothing to its operands, and still writes any
    file its script names (`w`, `W`, `s///w`) and runs any command it holds
    (`e`, `s///e`). The files are judged as any destination; a command is
    refused, as is a script read from a file or one this cannot read (#297).
    """
    if script_file:
        return (
            "`sed -f` runs a script read from a file, which can hold `w` and "
            "`e` commands this guard cannot see; give the script with `-e` "
            "(#297, docs/harness-boundaries.md)."
        )
    for script in scripts:
        if script is None:
            return (
                "`sed` is handed a script built by a command substitution, "
                "which can write a file or run a command; name the script "
                "(#297, docs/harness-boundaries.md)."
            )
        try:
            actions = sed_script_actions(script)
        except ValueError as problem:
            return (
                f"`sed` is handed a script this guard cannot read to its end "
                f"({problem}), and a `w` or `e` in it would write a file or "
                "run a command nothing judged; refusing (#297, "
                "docs/harness-boundaries.md)."
            )
        for letter, text in actions:
            if letter == "e":
                return (
                    "`sed` runs a command through an `e` command or an "
                    "`s///e` flag, and nothing here can say what it writes "
                    "(#297, docs/harness-boundaries.md)."
                )
            refusal = destination_offence(shlex.quote(text), command, "`sed`", "`sed`")
            if refusal is not None:
                return refusal
    return None


def above_working_directory(word):
    """The literal of the raw `word` when it is, or contains, the working
    directory or the home directory, else `None` (#297).

    A write started there reaches the protected trees without naming them:
    `find . -delete`, `git restore .` and `git clean -fd` each did while a
    path naming `.claude` was refused.
    """
    literal = target_literal(word)
    if literal is None:
        return None
    plain = os.path.normpath(literal.replace("\\", "/")).replace("\\", "/")
    if plain in (".", "..", "/") or plain.startswith("../") or (
            word.strip().startswith("~")):
        return literal
    if os.path.isabs(literal) or literal.startswith("/"):
        here = os.path.realpath(EVENT_CWD or os.getcwd())
        real = os.path.realpath(literal)
        if real == here or here.startswith(real.rstrip("/\\") + os.sep):
            return literal
    return None


def contains_working_directory(word, command):
    """The literal of the raw `word` when it is, or resolves to a parent of,
    the working directory or the home directory, else `None` (#309).

    The test `find -delete` applies to its starts, for the verbs that remove or
    move what they are given: `rm -rf ..` and `rm -rf ../..` reach `.claude/`
    and `package.json` without naming them. A path is **resolved**, so a
    sibling (`rm -rf ../old`) is not one; a word carrying a glob is refused by
    `destination_offence` already and is not judged here.
    """
    literal = target_literal(word)
    if literal is None:
        return None
    if "$" in literal:
        # Expanded as `destination_offence` expands it; a value nothing here
        # can read is refused rather than judged as the text that spells it.
        literal = expanded_target(literal, command)
        if literal is None:
            return word
    plain = literal.replace("\\", "/")
    if os.path.normpath(plain).replace("\\", "/") == "/" or (
            word.strip() in ("~", "~/")):
        return literal
    here = os.path.realpath(EVENT_CWD or os.getcwd())
    if word.strip().startswith("~"):
        # `~` and `~/x` are the home directory; `~+`, `~-` and `~user` are
        # other directories this guard does not resolve, so they are refused.
        if not re.match(r"~(?:/|$)", plain):
            return literal
        plain = os.path.expanduser(plain)
    real = os.path.realpath(plain if os.path.isabs(plain) else os.path.join(
        here, plain))
    if real == here or here.startswith(real.rstrip("/\\") + os.sep):
        return literal
    # The home directory itself, by whichever spelling reaches it, even from a
    # working directory outside it: `rm -rf $HOME` and `rm -rf /home/user`.
    if real == os.path.realpath(os.path.expanduser("~")):
        return literal
    return None


# The verbs whose operands are removed or moved away, so one that is or holds
# the working directory takes the protected trees with it (#309).
REMOVING_VERBS = frozenset({"mv", "rm", "rmdir", "shred", "unlink"})


# The git subcommands that overwrite or delete the paths they are given (#297).
GIT_PATH_WRITERS = frozenset({"restore", "checkout", "clean"})

# Per subcommand: the short options that take a value, and the long ones.
GIT_PATH_VALUE_OPTIONS = {
    "restore": ("s", ("--source", "--pathspec-from-file", "--conflict")),
    "checkout": ("bB", ("--orphan", "--conflict", "--pathspec-from-file")),
    "clean": ("e", ("--exclude",)),
}


def git_directory():
    """The common git directory of the working directory, or `None` outside a
    repository; one that cannot be read raises (#357).

    Read from the file system, as `application_tree` reads `.git`: a hook that
    ran `git` would inherit the very environment it judges, and a work tree
    set in it would answer for the wrong repository.
    """
    root = os.path.realpath(EVENT_CWD or os.getcwd())
    while True:
        dot = os.path.join(root, ".git")
        if os.path.isdir(dot):
            return dot
        if os.path.isfile(dot):
            with open(dot, encoding="utf-8") as handle:
                pointer = handle.read().strip()
            if not pointer.startswith("gitdir:"):
                raise ValueError(dot)
            gitdir = os.path.normpath(os.path.join(
                root, pointer[len("gitdir:"):].strip()))
            with open(os.path.join(gitdir, "commondir"),
                      encoding="utf-8") as handle:
                return os.path.normpath(os.path.join(
                    gitdir, handle.read().strip()))
        parent = os.path.dirname(root)
        if parent == root:
            return None
        root = parent


def names_a_ref(literal):
    """Whether `git checkout <literal>` may name a revision rather than a path.

    A revision is a ref (a branch, a tag, or a branch only a remote has, which
    git creates), `HEAD` and its kin, or anything shaped like one: an object id
    prefix, or a name with a revision operator. **Anything else is a path**,
    and a repository that cannot be read answers "not a ref", the direction
    that judges more (#357).
    """
    if re.fullmatch(r"[0-9A-Fa-f]{4,40}", literal) or re.search(
            r"[~^:@]", re.sub(r"^[A-Za-z]:[\\/]", "", literal)) or literal in (
            "HEAD", "FETCH_HEAD", "ORIG_HEAD", "MERGE_HEAD", "-"):
        return True
    try:
        gitdir = git_directory()
    except (OSError, ValueError):
        return False
    if gitdir is None:
        # Outside a repository `git checkout` writes nothing at all.
        return True
    name = literal.strip("/")
    if not name or ".." in name:
        return False
    for prefix in ("refs/heads", "refs/tags", "refs/remotes"):
        if os.path.isfile(os.path.join(gitdir, *prefix.split("/"), *name.split("/"))):
            return True
    remotes = os.path.join(gitdir, "refs", "remotes")
    try:
        for remote in os.listdir(remotes):
            if os.path.isfile(os.path.join(remotes, remote, *name.split("/"))):
                return True
    except OSError:
        pass
    try:
        with open(os.path.join(gitdir, "packed-refs"), encoding="utf-8") as handle:
            for line in handle:
                ref = line.split(" ", 1)[-1].strip()
                parts = ref.split("/", 3)
                if ref in (f"refs/heads/{name}", f"refs/tags/{name}",
                           f"refs/remotes/{name}") or (
                        parts[:2] == ["refs", "remotes"] and len(parts) == 4
                        and parts[3] == name):
                    return True
    except OSError:
        pass
    return False


def git_pathspec_offence(raw, name, separated, command, committed=False,
                         many=False):
    """The reason to refuse one pathspec operand of `git <name>`, or `None`."""
    writer = f"`git {name}`"
    literal = target_literal(raw)
    if literal.startswith(":") or any(char in literal for char in "*?["):
        return (
            f"{writer} is given the pathspec `{literal}`, which git reads as "
            "magic or a pattern, so the files it writes are not the string "
            "written here; name the paths (#297, docs/harness-boundaries.md)."
        )
    bare = re.fullmatch(r'"?\$(?:(\w+)|\{(\w+)\})"?', raw.strip())
    if bare and (bare.group(1) or bare.group(2)) in EXPANDABLE_VARIABLES:
        return (
            f"{writer} is given `{raw}`, a directory that is the checkout or "
            "holds it, so it writes the whole tree; name the files (#297, "
            "docs/harness-boundaries.md)."
        )
    above = above_working_directory(raw)
    if above is not None:
        return (
            f"{writer} is given `{above}`, which is or contains the working "
            "directory, so it writes the protected trees without naming them; "
            "name the paths (#297, docs/harness-boundaries.md)."
        )
    if committed:
        # `git checkout HEAD -- path` puts back the committed copy, which is how
        # this repository restores a file; another tree-ish brings in content
        # nobody reviewed on this branch, and stays judged.
        return None
    if name == "checkout" and not separated:
        # With no `--` the operand is a branch or a path and git decides which,
        # so a branch named `docs/x` must stay admitted: an application tree
        # counts only for a name that exists or carries an extension (a dot
        # and a letter: `spec-v1.2` is a branch, `x.ts` a file).
        last = re.split(r"[\\/]+", literal.rstrip("/\\"))[-1]
        named = protected_path(literal) or linked_protected_path(literal)
        # A name no ref carries cannot be the branch git would switch to, so
        # it is a path whether or not the file exists in the working tree:
        # `git checkout Makefile` restores a deleted file, and after a `cd`
        # `git checkout settings.json` restores a protected one (#357).
        not_a_ref = not names_a_ref(literal)
        if not named and (re.search(r"\.[A-Za-z][A-Za-z0-9]{0,5}$", last)
                          or not_a_ref
                          or os.path.exists(os.path.join(
                              EVENT_CWD or os.getcwd(), literal))):
            named = application_tree(literal)
        # A word the shell expands is not the name it is written as: `git
        # checkout $F` and `git checkout .claude{,}` are judged as paths.
        # A lone `$NAME` the command never assigns is the environment's, not
        # the command's, and `git checkout $BRANCH` is traffic this repository
        # writes; anything else the shell expands is judged.
        if not named and not (
                (shell_expands(raw) and not environment_variable(raw, command))
                or raw.strip().startswith("~")
                or (changes_directory(command)
                    and (many or not_a_ref or re.search(r"[./]", raw)))):
            return None
    return destination_offence(raw, command, writer, writer)


# The value of `-m`, `-am` or `--message` when it is one quoted string that
# expands nothing: a commit's message is text, and one that names `GIT_DIR` or
# says `source` relocates and evaluates nothing (#357).
INERT_MESSAGE = re.compile(
    r"""(?<=\s)(-[A-Za-z]*m|--message)(=|\s+)("[^"$`\\]*"|'[^']*')""")


def without_messages(command):
    """`command` with each inert message value of a `git commit`, `tag` or
    `merge` emptied. The run is read back to the last separator, and an option
    cluster that carries `c`, `e` or `s` is a shell's (`bash -cm 'script'`),
    never a message (#357)."""
    # Where each character stands: outside every quote, or inside one. **A
    # match is blanked only when it begins outside every quote** and the
    # command's quotes balance, so a `-m '` inside a double-quoted `bash -c`
    # script cannot pair its quote with a later one and hide the text between.
    outside, state, index = [], None, 0
    while index < len(command):
        char = command[index]
        outside.append(state is None)
        if char == "\\" and state != "'" and index + 1 < len(command):
            outside.append(state is None)
            index += 1
        elif state is None and char in "'\"":
            state = char
        elif state == char:
            state = None
        index += 1
    if state is not None:
        return command

    def blank(match):
        if not outside[match.start()] or (
                match.end() < len(command)
                and command[match.end()] not in " \t\n;&|)"):
            return match.group(0)
        before = command[:match.start()]
        run = before[max(before.rfind(mark) for mark in ";&|\n(`") + 1:]
        if re.search(r"\bgit\b.*\b(?:commit|tag|merge)\b", run) and not (
                match.group(1) != "--message"
                and re.search(r"[ces]", match.group(1))):
            return match.group(1) + match.group(2) + '""'
        return match.group(0)

    return INERT_MESSAGE.sub(blank, command)


def git_path_writer_offence(run, literals, command):
    """The reason to refuse a `git restore`, `checkout` or `clean`, or `None`.

    These write or delete the paths they are given, and a pathspec is judged as
    any other destination: `git checkout origin/main -- .claude/settings.json`
    put a branch's copy of a protected file in place, and `git clean -fd`, `git
    restore .` and `git checkout .` reach the protected trees without naming
    them. A branch checkout names no path and stays admitted, as does a restore
    of `--staged` alone and a `clean -n`. A run led by a reader is not judged
    unless git leads it (#297).
    """
    lead = next((literal for raw, literal in zip(run, literals)
                 if literal is not None and not ASSIGNMENT.match(raw)
                 and literal not in SHELL_KEYWORDS), None)
    if lead is not None and program_name(lead) != "git" and program_name(
            lead) in (READING_COMMANDS | DATA_ONLY_COMMANDS):
        return None
    # A computed word ahead of the verb (`$W git checkout ...`) is a wrapper
    # this guard cannot read, so git does not lead.
    computed = any(literal is None and not ASSIGNMENT.match(raw)
                   for raw, literal in zip(run, literals))
    led = (lead is not None and program_name(lead) == "git" and not computed)
    candidates = [index for index, literal in enumerate(literals)
                  if literal is not None and program_name(literal) == "git"]
    for start in (candidates[:1] if led else candidates):
        rest = literals[start + 1:]
        named = [word for word in rest if word is not None]
        stripped = after_global_options(named)
        name = verb_name(stripped[0]) if stripped else ""
        if name in GIT_PATH_WRITERS:
            break
        hidden = next((verb_name(word) for word in stripped[1:]
                       if not word.startswith("-")
                       and verb_name(word) in GIT_PATH_WRITERS), None)
        leading = named[:len(named) - len(stripped)]
        unknown = [word for word in leading if word.startswith("-")
                   and "=" not in word
                   and word not in GLOBAL_VALUE_FLAGS | BOOLEAN_GLOBALS]
        if unknown and hidden:
            # `--super-prefix x/ checkout ...`: the option's value is read as
            # the verb, and the real verb follows it.
            return (
                f"`git {hidden}` follows an option this guard does not know, "
                "which can take the verb's place; use only the global "
                "options it names (#297, docs/harness-boundaries.md)."
            )
    else:
        return None
    writer = f"`git {name}`"
    if not led:
        # Behind `xargs`, `find -exec`, `env -C` or `sudo`, the paths arrive
        # from elsewhere or the directory has moved, and the first `git` word
        # may be an operand (`sudo -u git git restore ...`).
        return (
            f"{writer} runs behind a wrapper, which can supply its paths on "
            "stdin, move its directory or hide which word is git; run it "
            "directly (#297, docs/harness-boundaries.md)."
        )
    if len(named) != len(rest):
        return (
            f"{writer} is handed a word built by a command substitution, so "
            "the paths it writes cannot be read from this command; name them "
            "(#297, docs/harness-boundaries.md)."
        )
    skipped = len(rest) - len(stripped)
    # A relocated work tree moves where git writes while the operand still looks
    # ordinary: `GIT_WORK_TREE=.claude git checkout HEAD -- settings.json`. The
    # environment arrives as assignment words ahead of git (or `env`'s), the
    # config as `-c core.worktree=`; an `export` in an earlier Bash call is
    # not seen, one anywhere in this command line is (docs/harness-boundaries.md).
    relocated = any(
        ASSIGNMENT.match(raw) and re.match(
            r"(?i)(GIT_WORK_TREE|GIT_DIR|GIT_COMMON_DIR|GIT_INDEX_FILE|GIT_CONFIG\w*)\+?=",
            raw)
        for raw in run[:start])
    # The names alone, in any reading of the command: `read GIT_WORK_TREE`,
    # `printf -v GIT_DIR` and `declare -n` write them with no `=`. Code that
    # builds a name, `eval` and `source` included, is refused beside a writer.
    relocation = re.compile(
        r"(?i)(?<!\w)(GIT_WORK_TREE|GIT_DIR|GIT_COMMON_DIR|GIT_INDEX_FILE"
        r"|GIT_CONFIG\w*)\b"
        r"|(?<![\w./-])(?:eval|source|trap)(?![\w./-])"
        r"|\b(?:export|declare|typeset|local|readonly|env)\b[^;&|\n]*(?:\$\(|`)"
        r"|\b(?:export|declare|typeset|local|readonly|env|read|mapfile|readarray)"
        r"\b(?:\s+[^\s;&|]+)*?\s+[^\s=;&|]*\$|printf\s+-v\s+\S*\$"
        r"|\b(?:export|declare|typeset|local|readonly)\b(?:\s+[^\s;&|]+)*?"
        r"\s+-\w*n\w*\b[^;&|\n]*\$"
        r"|\b(?:export|declare|typeset|local|readonly|env|read|mapfile|readarray)"
        r"\b(?:\s+[^\s;&|]+)*?\s+[^\s=;&|]*[{*?\[\\'\"`]"
        r"|printf\b[^;&|\n]*-\w*v\s*\S*[$\\{*?\[\\'\"`]"
        r"|\bgetopts\b[^;&|\n]*[$\\{*?\[\\'\"`]"
        r"|\bwait\b(?:\s+-\S+)*?\s+-\w*p\b[^;&|\n]*[$\\{*?\[\\'\"`]"
        r"|(?<![^\s;&|(){}!])\.(?:\s+(?:\d*[<>]+&?\s*[^\s;&|<>]+\s+)*[^\s;&|<>]|\$)"
        r"|<\(")
    relocated = relocated or any(
        relocation.search(text) for text in command_readings(
            without_messages(command)))
    if relocated or any(
            word.startswith(("-C", "--work-tree", "--git-dir"))
            or "core.worktree" in word.lower()
            for word in rest[:skipped]):
        return (
            f"{writer} runs under `-C`, `--work-tree`, `--git-dir`, "
            "`core.worktree` or a `GIT_WORK_TREE`, `GIT_DIR` or `GIT_CONFIG*` "
            "assignment, which moves the paths it writes away from the "
            "working directory this guard judges against; run it from the "
            "checkout (#297, docs/harness-boundaries.md)."
        )
    arguments = rest[skipped + 1:]
    raws = run[start + 1 + skipped + 1:]
    short_values, long_values = GIT_PATH_VALUE_OPTIONS[name]
    operands, operand_literals, flags = [], [], []
    separated, before, position, source = False, 0, 0, None

    def has(flag):
        # git takes any unambiguous prefix of a long option.
        return any(len(given) > 2 and flag.startswith(given) for given in flags)

    while position < len(arguments):
        raw, literal = raws[position], arguments[position]
        position += 1
        if separated or not literal.startswith("-") or literal == "-":
            operands.append(raw)
            operand_literals.append(literal)
        elif literal == "--":
            separated = True
            before = len(operands)
        elif literal.startswith("--"):
            option, equals, _ = literal.partition("=")
            flags.append(option)
            if name == "restore" and len(option) > 3 and "--source".startswith(
                    option):
                source = literal.partition("=")[2] if equals else (
                    arguments[position] if position < len(arguments) else None)
            elif name == "restore" and len(option) > 5 and (
                    "--no-source".startswith(option)):
                # git accepts the negated form, which resets the source to the
                # index; it is no longer HEAD.
                source = None
            if not equals and any(len(option) > 2 and full.startswith(option)
                                  for full in long_values):
                position += 1
        else:
            for offset, letter in enumerate(literal[1:]):
                flags.append("-" + letter)
                if letter in short_values:
                    if name == "restore" and letter == "s":
                        source = literal[offset + 2:] or (
                            arguments[position] if position < len(arguments)
                            else None)
                    if not literal[offset + 2:]:
                        position += 1
                    break

    if has("--pathspec-from-file"):
        return (
            f"{writer} reads its paths from a file this guard cannot read; "
            "name them (#297, docs/harness-boundaries.md)."
        )
    if name == "clean" and ("-n" in flags or has("--dry-run")):
        return None
    if name == "restore" and ("-S" in flags or has("--staged")) and not (
            "-W" in flags or has("--worktree")):
        return None
    if not operands:
        if name == "clean":
            return (
                f"{writer} with no pathspec removes untracked files from the "
                "whole working directory, the protected trees among them; "
                "name the paths (#297, docs/harness-boundaries.md)."
            )
        return None
    from_head = (name == "checkout" and separated and before == 1
                 and operand_literals[0] in ("HEAD", "@"))
    committed = from_head or (name == "restore" and source in ("HEAD", "@"))
    for raw in operands[before if from_head else 0:]:
        refusal = git_pathspec_offence(raw, name, separated, command, committed,
                                       len(operands) > 1)
        if refusal is not None:
            return refusal
    return None


def find_delete_offence(starts, files0, command):
    """The reason to refuse a `find -delete` over `starts`, or `None`.

    `-delete` removes everything its starting points reach, so a start that is
    the working directory, a parent of it or the home directory reaches the
    protected tree without naming it: `find . -delete` and `find .. -name x
    -delete` were admitted while `find .claude/hooks -delete` was refused. No
    starting point means the working directory. `-files0-from` reads the starts
    from a file this guard cannot read. A start under a checkout is judged as
    any other destination (#297).
    """
    if files0 or not starts:
        return (
            "`find -delete` has no starting point this guard can read — none "
            "(which means the working directory) or `-files0-from` — so what "
            "it removes cannot be placed. Name the starting points "
            "(#297, docs/harness-boundaries.md)."
        )
    for word in starts:
        above = above_working_directory(word)
        if above is not None:
            return (
                f"`find -delete` starts at `{above}`, which is or contains "
                "the working directory or the home directory, so it removes "
                "the agent's own machinery without naming it. Start below "
                "it, at a path that names what is to go "
                "(#297, docs/harness-boundaries.md)."
            )
        refusal = destination_offence(word, command, "`find`", "`find`")
        if refusal is not None:
            return refusal
    return None


def find_offence(words, command):
    """The reason to refuse a `find` that writes or deletes, or `None`.

    The words before the first expression word are the starting points (the
    current directory when there are none); `-H`, `-L`, `-P`, `-O<n>` and
    `-D <flags>` stand among them and are not paths. Only `-delete` judges the
    starting points, since only it acts on what they reach. A destination that
    is computed is refused by `destination_offence`; an action word that is
    itself computed (`find /tmp "$A"`) is not read, the same residual the other
    models carry in `docs/harness-boundaries.md`.
    """
    literals = [target_literal(word) for word in words]
    starts, destinations, deleting, expression = [], [], False, False
    files0 = False
    position = 0
    while position < len(words):
        raw, literal = words[position], literals[position]
        position += 1
        if not expression:
            if literal in ("-H", "-L", "-P") or (
                    literal is not None and literal.startswith("-O")):
                continue
            if literal == "-D":
                position += 1
                continue
            if literal == "--":
                continue
            if literal is None or not literal.startswith(("-", "(", "!", ",")):
                starts.append(raw)
                continue
            expression = True
        if literal in FIND_EXECUTORS:
            # The words up to the terminator are another program's, judged
            # as that program's by `writing_verb_offence`.
            while position < len(words) and literals[position] not in (";", "+"):
                position += 1
            position += 1
        elif literal in FIND_WRITING_ACTIONS and position < len(words):
            destinations.append(words[position])
            position += 1
        elif literal == "-delete":
            deleting = True
        elif literal == "-files0-from":
            files0 = True

    if deleting:
        refusal = find_delete_offence(starts, files0, command)
        if refusal is not None:
            return refusal
    for word in destinations:
        refusal = destination_offence(word, command, "`find`", "`find`")
        if refusal is not None:
            return refusal
    return None


def verb_offence(verb, words, command):
    """The reason to refuse `verb` run on the raw `words` after it, or `None`.

    **Options are parsed, not skipped by a leading dash**, because an option
    can carry the destination: `cp -t.claude/hooks x` writes into the hooks
    while no operand names them, and `cp x y -S .bak` puts a value where a
    last-operand rule would read the destination.
    """
    literals = [target_literal(word) for word in words]
    writer = kind = f"`{verb}`"
    model = WRITING_VERBS[verb]

    if model == "find":
        return find_offence(words, command)

    if model == "of":
        for raw, literal in zip(words, literals):
            if literal is None:
                return (
                    "`dd` is handed an operand built by a command substitution, "
                    "which can be its `of=`; refusing rather than admitting a "
                    "write nothing judged (#26, docs/harness-boundaries.md)."
                )
            if literal.startswith("of="):
                refusal = destination_offence(
                    _glued_raw(raw, literal, 3), command, writer, kind)
                if refusal is not None:
                    return refusal
        return None

    operands, targets, suffixes = [], [], []
    in_place = script_given = directory_mode = options_done = False
    scripts, script_file = [], False
    # **A hard link makes a source writable under the destination's name.**
    # `ln .claude/settings.json notes/alias` judged only `notes/alias`, and a
    # redirection to that admitted name then wrote the settings file's inode.
    # Raised by Copilot. A symbolic link is judged where it lands already —
    # `linked_protected_path` resolves it — so only the hard forms, `ln`
    # without `-s` and `cp -l`, judge their sources too.
    symbolic = hard_link = False
    short_values = SHORT_VALUE_OPTIONS.get(verb, "")
    long_values = LONG_VALUE_OPTIONS.get(verb, ())
    position = 0
    while position < len(words):
        raw, literal = words[position], literals[position]
        position += 1
        if literal is None or options_done or literal in ("-", "") or (
                not literal.startswith("-")):
            operands.append(raw)
            continue
        if literal == "--":
            options_done = True
            continue
        if literal.startswith("--"):
            name, equals, _ = literal.partition("=")

            def abbreviates(option):
                return len(name) > 2 and option.startswith(name)

            if verb in TARGET_OPTION_VERBS and abbreviates("--target-directory"):
                if equals:
                    targets.append(_glued_raw(raw, literal, len(name) + 1))
                elif position < len(words):
                    targets.append(words[position])
                    position += 1
                continue
            if verb == "sed" and abbreviates("--in-place"):
                in_place = True
                if equals:
                    suffixes.append(literal[len(name) + 1:])
                continue
            if verb == "ln" and abbreviates("--symbolic") and not abbreviates(
                    "--suffix"):
                symbolic = True
                continue
            if verb == "cp" and abbreviates("--link"):
                hard_link = True
                continue
            if verb == "install" and abbreviates("--directory"):
                directory_mode = True
                continue
            if verb == "sed" and abbreviates("--expression"):
                script_given = True
                scripts.append(literal[len(name) + 1:] if equals else (
                    literals[position] if position < len(words) else ""))
            elif verb == "sed" and abbreviates("--file"):
                script_given = script_file = True
            if not equals and any(abbreviates(option) for option in long_values):
                position += 1
            continue
        letters = literal[1:]
        for offset, letter in enumerate(letters):
            cut = offset + 2
            rest = letters[offset + 1:]
            if verb in ("sed", "perl") and letter == "i":
                in_place = True
                if rest:
                    suffixes.append(rest)
                break
            if verb == "ln" and letter == "s":
                symbolic = True
                continue
            if verb == "cp" and letter == "l":
                hard_link = True
                continue
            if verb in TARGET_OPTION_VERBS and letter == "t":
                if rest:
                    targets.append(_glued_raw(raw, literal, cut))
                elif position < len(words):
                    targets.append(words[position])
                    position += 1
                break
            if verb == "install" and letter == "d":
                directory_mode = True
                continue
            script_letter = (verb == "sed" and letter in "ef") or (
                verb == "perl" and letter in "eE")
            if script_letter:
                script_given = True
                if verb == "sed" and letter == "e":
                    scripts.append(rest if rest else (
                        literals[position] if position < len(words) else ""))
                elif verb == "sed":
                    script_file = True
            if script_letter or letter in short_values:
                if not rest:
                    position += 1
                break
            if verb == "perl" and letter in "CDFIMdmx":
                break

    if verb == "sed":
        # A word the shell expands is not the option or the script it will be:
        # `-e"$(cat s)"` is read as an operand, and `s/a/$X/` as plain text.
        for raw in words:
            if raw == "--" and script_given:
                # What follows a literal `--` is a file, and the script is
                # already given, so an expansion there cannot be either.
                break
            if shell_expands(raw):
                return (
                    f"`sed` is handed `{raw}`, which the shell expands, so "
                    "the options and the script it receives are not the ones "
                    "written here and may write a file or run a command; "
                    "name them literally (#297, docs/harness-boundaries.md)."
                )
        if not script_given and operands:
            scripts.append(target_literal(operands[0]))
        refusal = sed_script_offence(scripts, script_file, command)
        if refusal is not None:
            return refusal

    unreadable = [raw for raw, literal in zip(words, literals)
                  if _unreadable_word(literal, command)]
    if model == "every" or directory_mode or hard_link or (
            verb == "ln" and not symbolic):
        judged = operands + targets
    elif model == "destination":
        # **A word whose value is not in the source can be an option**, and
        # an option can be the destination: `cp "$X" y` is `cp -t .claude y`
        # when `X` says so. The operands this model does not judge are read
        # for that reason alone.
        if unreadable:
            return (
                f"`{verb}` is handed `{unreadable[0]}`, whose value is built "
                "by an expansion or a substitution this guard cannot read, and "
                "it can be an option that moves the destination; refusing "
                "rather than judging the words instead of the argv (#26, "
                "docs/harness-boundaries.md)."
            )
        if targets:
            judged = targets
        elif len(operands) > 1:
            judged = operands[-1:]
        elif operands and verb == "ln":
            # One operand links it into the working directory by its basename.
            name = os.path.basename(target_literal(operands[0]).rstrip("/\\"))
            judged = [shlex.quote(name)]
        else:
            judged = []
    elif in_place:
        judged = operands if script_given else operands[1:]
        # **The backup is a second file written, and its name is built from
        # the suffix.** `sed -i.json -e 1 package` backs `package` up as
        # `package.json`, and GNU sed and Perl both replace a `*` in the
        # suffix with the file's name — `-i'.claude/*'` writes the backup
        # into the machinery. Raised by Copilot. A suffix is appended and
        # judged; one carrying `*` or a separator is refused rather than
        # modelled.
        files = judged
        for suffix in suffixes:
            if any(char in suffix for char in "*/\\"):
                return (
                    f"`{verb} -i` is given the backup suffix `{suffix}`, which "
                    "carries a `*` or a path separator, so the backup is "
                    "written somewhere other than beside the file; refusing "
                    "rather than modelling where (#26, "
                    "docs/harness-boundaries.md)."
                )
            judged = judged + [word + shlex.quote(suffix) for word in files]
    else:
        judged = []

    if verb in REMOVING_VERBS:
        # What `mv` moves is every operand but the last, or all of them when
        # `-t` names the destination; the destination is judged below.
        gone = operands[:-1] if verb == "mv" and not targets else operands
        for word in gone:
            above = contains_working_directory(word, command)
            if above is not None:
                return (
                    f"`{verb}` is given `{above}`, which is or contains the "
                    "working directory or the home directory, so it removes "
                    "the agent's own machinery without naming it. Name what "
                    "is to go, below the working directory "
                    "(#309, docs/harness-boundaries.md)."
                )

    for word in judged:
        refusal = destination_offence(word, command, writer, kind)
        if refusal is not None:
            return refusal
    return None


def git_writes(literals):
    """Whether a run led by `git` is a subcommand that writes its operands (#278)."""
    words = [word for word in literals if word is not None]
    for index, word in enumerate(words):
        if program_name(word) == "git":
            return subcommand_of(words[index + 1:]) in GIT_WRITING_SUBCOMMANDS

    return False


def writing_verb_offence(command):
    """The reason to refuse a writing verb's destination in `command`, or `None`.

    **The verb is looked for anywhere in the run, not only in the lead**,
    because a wrapper puts it in the middle — `sudo tee`, `env cp`,
    `find -exec cp`, `busybox mv` — and the wrapper list is the one this file
    has refused to keep every time, since it fails open on the first name
    nobody listed. What that costs is judged away by the lead instead: a run
    led by a reader or a printer — `grep -n tee .claude/…`, `git log -- cp`,
    `echo rm …` — is inspecting or printing those words and cannot run them.

    **`xargs` in front of a verb is refused outright.** Its operands arrive on
    stdin — `git ls-files .claude | xargs rm` names no path at all — so there
    is nothing in the source to judge.
    """
    for run in raw_runs(command):
        literals = [target_literal(word) for word in run]
        refusal = git_path_writer_offence(run, literals, command)
        if refusal is not None:
            return refusal
        lead = next((literal for raw, literal in zip(run, literals)
                     if not ASSIGNMENT.match(raw)
                     and literal not in SHELL_KEYWORDS), None)
        # `git` is the only reader that writes, and only when it leads: a
        # `git` further along (`grep -r git rm`, `cat git mv x`) is an
        # operand the reader prints, not a subcommand it runs (#297).
        if lead is not None and program_name(lead) in (
                READING_COMMANDS | DATA_ONLY_COMMANDS) and not (
                program_name(lead) == "git" and git_writes(literals)):
            continue
        for index, literal in enumerate(literals):
            if literal is None or ASSIGNMENT.match(run[index]):
                continue
            verb = program_name(literal)
            if verb not in WRITING_VERBS:
                continue
            before = [program_name(word) for word in literals[:index] if word]
            if "xargs" in before:
                return (
                    f"`xargs` runs `{verb}` on names it reads from stdin, so "
                    "the files it writes are not in this command; refusing "
                    "rather than admitting a write nothing judged (#26, "
                    "docs/harness-boundaries.md)."
                )
            words = run[index + 1:]
            if any(word in FIND_EXECUTORS for word in literals[:index]):
                for cut, word in enumerate(literals[index + 1:]):
                    if word in (";", "+"):
                        words = words[:cut]
                        break
            refusal = verb_offence(verb, words, command)
            if refusal is not None:
                return refusal
    return None


def expandable_regions(command):
    """Every part of `command` the shell would expand, as `(text, quotes)`.

    Substitution extraction used to run over the raw string with a quote tracker
    of its own and no notion of heredocs or comments, which made it disagree
    with the rest of the guard in both directions at once — verified, both ways:

    | Command | bash | the guard was |
    |---|---|---|
    | `git commit -F - <<'EOF'` … `$(git push origin +HEAD:main)` | does not expand | refusing |
    | `git commit -F - <<EOF` … `don't $(git push origin +HEAD:main)` | expands | admitting |

    The second is the one that matters: an apostrophe in the body is a quote to
    a raw scanner and a character to bash, so the live substitution was skipped
    and the push ran. `quotes` is what carries that — inside a heredoc body
    there are no quotes to honour, only expansions to perform.

    The command line itself arrives with bodies and comments already gone, so a
    `$(…)` the shell would never reach cannot be judged as though it would.
    """
    line, regions, cursor = [], [], 0
    for start, end, expands in heredoc_spans(command):
        line.append(command[cursor:start])
        if expands:
            regions.append((command[start:end], False))
        cursor = end
    line.append(command[cursor:])
    return [(strip_comments("".join(line)), True)] + regions


def substituted_gh_offence(inner):
    """The reason to refuse `gh` run inside a substitution, or `None`.

    **A substitution runs before the command that holds it, and the grant is
    judged on the holder.** `bash .claude/scripts/gh-pr-merge.sh 1 $(gh pr
    merge --merge 42 --admin)` matches the helper's prefix grant, and the merge
    happens while bash is still building the helper's argv — before any of the
    helper's own checks. The same holds on a globally approved `ls`. No command
    here runs `gh` inside a substitution, so every `gh` there is refused rather
    than its subcommand judged. Raised by Copilot.

    **Every word, not the first program.** The first form stopped at the
    first program it met, so `$(printf ok; gh pr merge 42 --admin)` reached
    `printf` and was admitted, and `$(bash -c 'gh …')` hid it one level down.
    Raised by Copilot. So the body is read after quote removal and split on
    whitespace and shell punctuation, and a `gh` word anywhere in it refuses —
    over-refusing a substitution that merely mentions `gh`, which nothing here
    needs to do.
    """
    if contains_gh_word(inner):
        return (
            "a command substitution runs `gh`, and it runs before the "
            "command that holds it is checked against its grant — so a "
            "fixed helper's own validation never sees it. Nothing here "
            "runs `gh` inside a substitution; call the helper directly."
        )
    if runs_unmodelled_program(inner):
        return (
            "a command substitution runs a program outside the short list "
            "whose effects this guard models — a script path, an interpreter, "
            "`awk` or `sed` — and it runs before the command that holds it is "
            "checked against its grant. Run it as its own command, where the "
            "permission rules see it."
        )
    return None


def runs_unmodelled_program(text):
    """Whether any command run in `text` is led by a program outside the list.

    Led means the first word past assignments and the wrappers that run their
    argument — `env`, `command`, `exec`, `nohup`, `time`, `xargs` — so wrapping
    a program does not hide it. A word carrying a `/` is refused whatever its
    basename, since `./tools/cat` is the branch's file and not `cat`.
    """
    unquoted = re.sub(r"\$?[\"']|\\", "", text)
    for run in re.split(r"[;&|()\n`]+", unquoted):
        for word in run.split():
            if ASSIGNMENT.match(word) or word.startswith("-"):
                continue
            # Splitting at every parenthesis leaves the tail of a nested
            # `${…}` or `$(…)` as its own "run": a bare `}` or `$` is that
            # tail, not a program.
            if re.fullmatch(r"[{}$]+", word):
                continue
            name = program_name(word)
            if name in {"builtin", "command", "env", "exec", "nohup", "time",
                        "xargs"}:
                continue
            if "/" in word or "\\" in word or name not in SUBSTITUTION_PROGRAMS:
                return True
            break
    return False


def names_gh(word):
    """Whether `word` is `gh`, or a pattern or brace expansion bash makes `gh`."""
    name = program_name(word)
    for candidate in brace_alternatives(name):
        if candidate == "gh" or (any(char in candidate for char in "*?[")
                                 and fnmatch.fnmatchcase("gh", candidate)):
            return True
    return False


def contains_gh_word(text):
    """Whether `text`, with its quoting removed, may run `gh`.

    **A literal word misses what bash expands first.** `/usr/bin/[g]h` holds no
    `gh` and bash runs `gh`. Raised by Copilot. So a word is compared as a
    pattern and a brace expansion would produce, anywhere in the body; and a
    program word whose value is not in the source at all — a variable, a
    backtick, a range or an extglob — is refused where a command stands,
    since it may be `gh` too.
    """
    unquoted = re.sub(r"\$?[\"']|\\", "", text)
    if any(names_gh(word) for word in re.split(r"[\s;&|()<>`]+", unquoted)):
        return True
    for run in re.split(r"[;&|()\n`]+", unquoted):
        for word in run.split():
            if ASSIGNMENT.match(word) or word.startswith("-"):
                continue
            if program_name(word) in {"builtin", "command", "env", "exec",
                                      "nohup", "time", "xargs"}:
                continue
            if "$" in word or "{" in word or re.search(r"[@+!?*]\(", word) or (
                    any(char in word for char in "*?[")):
                return True
            break
    return False


def process_substitution_bodies(command):
    """The body of every `<(…)` and `>(…)` in `command`, by paren balance."""
    bodies = []
    # Only where bash would perform one: `"see <(foo)"` inside quotes is text,
    # and so is a heredoc body, which the caller strips before asking.
    quoted = {index for index, in_quotes, in_comment in shell_positions(command)
              if in_quotes or in_comment}
    for match in re.finditer(r"[<>]\(", command):
        if match.start() in quoted:
            continue
        depth, index = 1, match.end()
        while index < len(command) and depth:
            depth += {"(": 1, ")": -1}.get(command[index], 0)
            index += 1
        bodies.append(command[match.end():index - 1 if depth == 0 else index])
    return bodies


def substitutions(command, quotes=True):
    """Every `$(...)` and backtick body in `command`, innermost included.

    These are COMMANDS the shell executes, and `shlex` hands them back as one
    quoted token — so `git log "$(git push origin +HEAD:main)"` contains no
    standalone `git` for the segment scan to find. Extracted and judged in
    their own right.

    `quotes` is false for a heredoc body, where `'` is an ordinary character
    rather than a quote. See `expandable_regions` for what that cost.
    """
    found = []
    index = 0
    # One model of bash's quoting, shared: this scan used to keep its own,
    # which never learned that `$'…'` takes escapes — so `: $'x\\''; git log
    # "$(git push origin +HEAD:main)"` ran the nested push while this state
    # machine closed at the escaped quote, reopened at the real closer, and
    # never saw the `$(`. Raised in review; verified allowed. See
    # `quote_states`.
    #
    # **`$(` is live inside DOUBLE quotes**, which is the whole shape of the
    # bypass this pass exists for, so only the single-quoted state stops it —
    # and the one branch below that DOES need the double-quoted state reads
    # it from the same list rather than tracking a second thing.
    states = quote_states(command, quotes=quotes)
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            # **An apostrophe inside double quotes opens nothing**, and reading
            # one as a quote suppressed every substitution after it:
            # `git log "don't $(git push origin +HEAD:main)"` runs the push, and
            # the scanner entered single-quote state at `don't`, never saw the
            # `$(`, and handed `shlex` an opaque quoted argument. Raised in
            # review; verified allowed, on `main` as well — and settled here by
            # asking `quote_states` rather than by tracking a second flag,
            # which is the same answer one layer up.
            index += 1
            continue
        if char == "\\":
            # `\$(x)` is a literal `$(` to bash, on the command line and in an
            # unquoted heredoc body alike. Skipping the escaped character keeps
            # the guard off a substitution the shell will never perform.
            index += 2
            continue
        if command.startswith("$(", index):
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if (quotes and states[index] != "double"
                and (command.startswith("<(", index)
                     or command.startswith(">(", index))):
            # **A process substitution is a command the shell runs**, and until
            # the redirection strip could consume one it was reached only by
            # the run splitter — which sees it while it stands as its own run
            # and not once it is part of a redirect target. Both halves of that
            # are now true in one place.
            #
            # **`quotes` is false for a heredoc BODY, and a body performs no
            # process substitution** — parameter, command and arithmetic
            # expansion only. Reading one there made literal prose executable,
            # so a heredoc quoting `<(git push …)` as an example was refused.
            # Raised in review; measured, and it is the over-refusal this
            # file's own docstring says gets a guard turned off. The flag is
            # reused rather than a second one added, because it already means
            # "this region is a command line" everywhere it is passed.
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if command.startswith("${", index) and command[index + 2:index + 3] in (
                " ", "\t", "\n", "|"):
            # **bash 5.3's function substitution runs a command**, where every
            # other `${…}` expands a parameter and runs nothing. `${ cmd; }`
            # and `${| cmd; }` are the two spellings, and the character after
            # the brace is what separates them from `${VAR}`.
            #
            # **This host is 5.2.26 and does not support it** — measured,
            # `bad substitution` — so it is closed BEFORE it is reachable
            # rather than after. An exemption resting on a version is one that
            # expires silently, and this file already carries that lesson about
            # a hook directory that was safe until it was not.
            end = _closing_brace(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end].lstrip("| \t\n"))
            index = end + 1
            continue
        if char == "`":
            # `find` ignored escapes, and a `\`` is a literal backtick to bash
            # rather than a terminator. Raised in review. **The reported
            # example is a bash SYNTAX ERROR** — measured, `unexpected EOF
            # while looking for matching` — so it was never a live bypass; the
            # scan is corrected anyway, because agreeing with the shell about
            # where a substitution ends is the property, not the one input that
            # exposed it.
            end = index + 1
            while end < len(command):
                if command[end] == "\\" and end + 1 < len(command):
                    end += 2
                    continue
                if command[end] == "`":
                    break
                end += 1
            if end >= len(command):
                break
            # **An escaped backtick is how the legacy form NESTS**, so skipping
            # the escape and handing the body on unchanged skipped it twice:
            # `git log "`echo \`git push origin +HEAD:main\``"` runs the push
            # — measured — and the inner substitution was invisible to both
            # passes. Unescaping on the way down is what makes the recursion
            # see the nested command as a command.
            found.append(command[index + 1:end].replace("\\`", "`"))
            index = end + 1
            continue
        index += 1
    return found


def _closing_brace(command, start):
    """Index of the `}` closing a function substitution, or None.

    The same quoting `_closing_paren` reads, one bracket over, and from the
    same place: `quote_states`. Written as its own function rather than
    parameterised, because the two differ in what nests inside them and a
    shared one would have to be told.

    **Over `command[start:]`, not over `command`.** A substitution body is
    re-parsed as a fresh command line, so the outer context's quoting does not
    reach inside one — asking about absolute positions would mark the whole
    body of `"$(printf x)"` as double-quoted and lose its own closer.
    """
    states = quote_states(command[start:])
    depth, index = 1, start
    while index < len(command):
        char = command[index]
        if states[index - start] in ("single", "double", "comment"):
            # A `}` inside a quote or a comment closes nothing — a function
            # substitution's body is a command list too. Raised in review, one
            # bracket over.
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            index += 2
            continue
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if not depth:
                return index
        index += 1
    return None


def _closing_paren(command, start):
    """Index of the `)` closing a substitution opened before `start`, or None.

    **Quotes are tracked while balancing**, because a paren counter that reads
    raw characters closes early on a quoted one:
    `git log "$(printf ')'; git push origin +HEAD:main)"` ended extraction at
    the `)` inside `'…'`, leaving the push hidden in the outer token. Raised in
    review; verified allowed.

    **And the tracking is `quote_states`', not a seventh copy of it.** This
    function kept its own, which never learned that `$'…'` takes escapes, so
    `git log "$( : $'x\''; git push origin +HEAD:main)"` closed and reopened on
    the wrong quotes and returned None — no substitution was extracted, `shlex`
    kept the outer one opaque, and the push ran. Raised in review; verified
    allowed, in the round after the five siblings were consolidated and this
    one was missed.

    **Over `command[start:]`, not over `command`.** A substitution body is
    re-parsed as a fresh command line, so the outer context's quoting does not
    reach inside one — asking about absolute positions would mark the whole
    body of `"$(printf x)"` as double-quoted and lose its own closer.
    """
    states = quote_states(command[start:])
    depth, index = 1, start
    while index < len(command):
        char = command[index]
        if states[index - start] in ("single", "double", "comment"):
            # **A substitution's body is a command list, so `#` opens a comment
            # inside it and a `)` in that comment closes nothing.**
            # `git log "$(echo ok # )` / `git push origin +HEAD:main)"` ended
            # extraction at the commented paren and left the push in the outer
            # token — measured, bash runs it. Raised in review. A quoted paren
            # closes nothing either, which is this function's first fix.
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            # An unquoted `\)` is a literal paren to bash, so counting it closed
            # the substitution early and hid the rest of it in the outer token:
            # `git log "$(printf \); git push origin +HEAD:main)"`. The escape
            # was handled inside double quotes and nowhere else. Raised in
            # review; the bash behaviour measured — `printf` receives the paren
            # and the push runs.
            index += 2
            continue
        if command.startswith("$(", index):
            depth += 1
            index += 2
            continue
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if not depth:
                return index
        index += 1
    return None


# A shell invoked with `-c` runs its argument as a command line, and `eval` runs
# the concatenation of its own. Both hand the guard a command it must read as
# one rather than as data.
EVALUATORS = {"bash", "sh", "dash", "zsh", "ksh"}

# `-c`, and the bundles that carry it — `bash -xc <script>` and `bash -cx
# <script>` alike. **`c` need not come last**, which the first version of this
# required: `bash -cx 'git push origin +HEAD:main'` runs the push, measured,
# and matched nothing. Raised in review. A long option is never the script
# introducer, so `--` forms are left alone.
SCRIPT_FLAG = re.compile(r"^-[A-Za-z]*c[A-Za-z]*$")

# The helpers that read their stdin as DATA — a title and a body — named as a
# shell's script operand, in the one spelling the grants use. No quote, `$`,
# glob, backslash or `..` can appear in it, so the word bash sees is the word
# typed. **Only these two, not every helper**: the guard can read a heredoc,
# and exempting one throws that reading away, which is right only for a
# helper known to consume stdin as text. Raised by Copilot on PR #36.
HELPER_SCRIPT = re.compile(
    r"^(?:\./)?\.claude/scripts/gh-(?:sweep-)?issue-create\.sh$")


# Windows resolves `git.exe`, `GIT.EXE` and `C:/Git/bin/git.exe` to one
# program, and this repository is developed on Windows.
EXECUTABLE_SUFFIXES = (".exe", ".cmd", ".bat", ".com")


def program_name(token):
    """The program `token` names, normalised for comparison.

    **The segment scan matched the literal `git` and a `/git` suffix**, so
    `git.exe push origin +HEAD:main` walked straight past it — and so did
    `bash.exe -c`. Verified on this host: `git.exe --version` and
    `bash.exe -c` both run. Found by probing the shapes adjacent to a fix,
    which is also how the platform came up: every case in this file had been
    written in POSIX spelling on a machine that answers to both.

    Lower-cased because Windows paths are case-insensitive. On a system where
    they are not, `GIT` names nothing and refusing it costs nothing.
    """
    name = re.split(r"[\\/]", token)[-1].lower()
    for suffix in EXECUTABLE_SUFFIXES:
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


# `NAME=value` before a command sets a variable for it and is not the command.
# **Four spellings, and the first version of this knew one.** Bash reads
# `NAME=value`, `NAME+=value`, `NAME[i]=value` and `NAME[i]+=value` all as
# assignment prefixes, so `X+=1 printf 'git p%ssh origin +HEAD:main' u | bash`
# ran the push while both printer passes took `X+=1` for the command word and
# left the run alone. Raised in review; verified allowed, with the `arr[0]=v`
# form beside it.
ASSIGNMENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]*\])?\+?=")


def leading_command(run):
    """The command word of `run`, past any assignment prefix.

    **`X=1 bash` is a run led by `bash`**, and reading the first token instead
    made it a run led by `X=1`: `X=1 bash <<<'git push origin +HEAD:main'` had
    its here-string stripped as an ordinary redirect target, the evaluator scan
    then saw a `bash` with no script, and the push ran. Raised in review;
    verified allowed.

    The same reading is owed to the printer half — `X=1 echo … | bash` — and to
    the data-only exemption, which is why this is one function rather than a
    test repeated at each site.
    """
    for token in run:
        if not ASSIGNMENT.match(token):
            return token
    return ""


def reads_stdin_as_script(words):
    """Whether a run made of `words` will EXECUTE what arrives on its stdin.

    **The test used to be that the run's LEADING word is a shell**, and a
    wrapper in front of one defeated it: `echo 'git push origin +HEAD:main' |
    command bash` runs the push, and so does the `env bash` spelling, while
    the leading word is `command` or `env` and no shell was found. Raised in
    review; both verified allowed, and `env` found beside the one that was
    reported.

    **So the shell is looked for anywhere in the run, and the exemption is the
    allow-list rather than the wrapper set.** Enumerating the wrappers that DO
    exec their argument is the direction `DATA_ONLY_COMMANDS` argues against in
    its own comment — it fails open on the first one nobody thought of, and
    `command`, `env`, `nohup`, `nice`, `stdbuf`, `setsid`, `timeout`, `ionice`
    and `chrt` are nine before anyone has looked hard. Reading any shell name
    in the run costs an over-refusal instead, and it costs one only in the
    shape `echo '…git push…' | grep bash`, because the printer half of the
    pipeline pass has to match first.

    A run carrying `-c` reads its script from the argv rather than from stdin,
    and `evaluated_scripts` judges that channel at any position already.
    `_helper_reads_stdin_as_data` is the one exemption, and it is taken where
    the run's position in the command is known rather than here.
    """
    body = [word for word in words if not ASSIGNMENT.match(word)]
    if not body or program_name(body[0]) in DATA_ONLY_COMMANDS:
        return False
    for position, word in enumerate(body):
        if program_name(word) not in EVALUATORS:
            continue
        # **A `-c` before the shell is the WRAPPER's option**, and reading the
        # whole run for one confused the two: `ionice -c 2 bash` runs bash on
        # its stdin, `-c` there being the scheduling class, and the run was
        # dismissed as carrying its own script. Raised in review; verified
        # allowed. Only what follows the shell token can be the shell's
        # script flag.
        if not any(SCRIPT_FLAG.match(element) for element in body[position + 1:]):
            return True
    return False


def _run_words(command, start, end, ordinary):
    """`command[start:end]` split into words on its unquoted metacharacters.

    **A redirection operator is kept as a word of its own.** Dropping it made
    `bash < .claude/scripts/a.sh <<'EOF'` read as a shell with a script
    operand, when the helper is a redirection target and the heredoc, the last
    stdin redirection, is what bash runs.
    """
    words, index = [], start
    while index < end:
        while index < end and command[index] in " 	":
            index += 1
        cursor = index
        while cursor < end and ordinary[cursor] and command[cursor] in "<>":
            cursor += 1
        if cursor > index:
            words.append(command[index:cursor])
            index = cursor
            continue
        while cursor < end and not (
                ordinary[cursor] and command[cursor] in METACHARACTERS):
            cursor += 1
        if cursor > index:
            words.append(command[index:cursor])
        index = cursor if cursor > index else index + 1
    return words


def _run_bounds(command, position, ordinary):
    """The half-open span of the command run containing `position`."""
    start = position
    while start > 0 and not _ends_run(command, start - 1, ordinary):
        start -= 1
    end = position
    while end < len(command) and not _ends_run(command, end, ordinary):
        end += 1
    return start, end


def _ends_run(command, index, ordinary):
    """Whether the character at `index` ends the command run it is in.

    **The `&` of a redirection operator is not a run boundary (#37).** Every
    unquoted `&` was read as one, so in `bash >&2 <<'EOF'` the run was taken to
    begin after the `&`, its words were `2` alone, no shell owned the heredoc,
    and bash ran the body. `<&`, `N>&M`, `&>` and `&>>` did the same, and so
    did `cat <<'EOF' 2>&1 | bash`, whose run ended before the pipe it feeds.
    Found by Copilot on PR #36; all measured allowed.

    Bash's lexer is greedy from the left, which settles each `&` by its two
    neighbours: after an unquoted `<` or `>` it is the second half of `<&` or
    `>&`; before an unquoted `>` it opens `&>` — unless an `&` or a `|` in front
    has already claimed it as `&&` or `|&`, which are separators. A quoted or
    escaped neighbour is a word character and claims nothing, so an `&` after
    an escaped `>` still ends the run.

    **The escape is counted here rather than read from `ordinary`**, because
    `stdin_scripts` builds a mask that calls an escaped character ordinary and
    tracks the escape beside it. Read from that mask alone, `echo \\>& bash
    <<'EOF'` — a backgrounded echo, then a shell running the heredoc — joined
    into one run led by `echo`, and a shape refused before this fix was
    admitted by it. Measured while writing it.

    **The same holds for the character being judged.** `bash 2>\\&1 <<'EOF'`
    sends stderr to a file named `&1` and runs the heredoc, and an escaped `;`
    or `|` is as much a word character; each split the run at a character bash
    does not treat as a control operator. Found by the adversarial pass over
    this fix, and allowed on `main` too.

    **A neighbour is the next character bash tokenises, not the next byte.**
    Bash removes a backslash-newline before it splits tokens, so
    `bash >\\<newline>&2 <<'EOF'` is `bash >&2 <<'EOF'` and runs the heredoc,
    and `&\\<newline>>` is `&>`; reading the byte beside the `&` saw the
    newline and ended the run. Raised by Copilot on PR #38; measured under
    Git Bash, and allowed on `main` too.
    """
    if not (_unescaped(command, index, ordinary)
            and command[index] in RUN_SEPARATORS):
        return False
    if command[index] != "&":
        return True
    before = _neighbour(command, index, -1, ordinary)
    after = _neighbour(command, index, 1, ordinary)
    if before in ("<", ">"):
        return False
    return not (after == ">" and before not in ("&", "|"))


def _unescaped(command, index, ordinary):
    """Whether `command[index]` is shell syntax: ordinary, and not the
    character an odd run of unquoted backslashes in front of it escapes."""
    slashes = 0
    while (index - slashes > 0 and ordinary[index - slashes - 1]
           and command[index - slashes - 1] == "\\"):
        slashes += 1
    return ordinary[index] and slashes % 2 == 0


def _neighbour(command, index, step, ordinary):
    """The syntax character beside `index` in direction `step` (-1 or 1), past
    any line continuations, or "" where that character is quoted or escaped.

    A backslash straight after an unescaped `&` is unquoted and unescaped
    itself, so a backslash-newline found walking forwards is always a
    continuation; walking backwards, the newline's own escape is counted."""
    cursor = index + step
    if step < 0:
        while (cursor > 0 and command[cursor] == "\n"
               and _continues(command, cursor)):
            cursor -= 2
        return (command[cursor]
                if cursor >= 0 and _unescaped(command, cursor, ordinary) else "")
    while (cursor + 1 < len(command) and command[cursor] == "\\"
           and command[cursor + 1] == "\n"):
        cursor += 2
    return command[cursor] if cursor < len(command) and ordinary[cursor] else ""


def _continues(command, newline):
    """Whether the newline at `newline` is escaped by an odd run of backslashes,
    counted whatever the mask says of them: both masks mark them differently."""
    slashes = 0
    while newline - slashes > 0 and command[newline - slashes - 1] == "\\":
        slashes += 1
    return slashes % 2 == 1


def forwards_to_evaluator(command, position, ordinary):
    """Whether the run at `position` writes into a shell later in its pipeline.

    **A heredoc belongs to the run that opens it and its BYTES belong to
    whatever is downstream of the pipe.** `cat <<'EOF' | bash` with a push in
    the body runs it: the opener is `cat`'s, so `stdin_scripts` yielded
    nothing, and `strip_heredocs` then removed the body — the only copy of the
    script — before anything else could look. The here-string spelling
    `cat <<<'git push origin +HEAD:main' | bash` fails the same way. Raised in
    review; both verified allowed, and both live on `main`.

    Only a `|` carries stdout onward, so `||` ends the walk rather than
    continuing it, and a `)` between the run and the pipe is stepped over
    because a subshell writes into the pipe exactly as a bare run does.
    """
    _, index = _run_bounds(command, position, ordinary)
    while index < len(command):
        while index < len(command) and (
                command[index] in " 	"
                or (ordinary[index] and command[index] == ")")):
            index += 1
        if not (index < len(command) and ordinary[index]
                and command[index] == "|") or command.startswith("||", index):
            return False
        index += 2 if command.startswith("|&", index) else 1
        start = index
        while index < len(command) and not _ends_run(command, index, ordinary):
            index += 1
        if reads_stdin_as_script(_run_words(command, start, index, ordinary)):
            return True
    return False


def _helper_reads_stdin_as_data(command, start, words):
    """Whether the run at `start`, made of `words`, is a shell handed one of the
    issue helpers as its script file — so its stdin is that helper's data.

    **A shell handed a script FILE reads its stdin as data, and the stdin pass
    refused every hand-filed issue that quoted code (#33).** `bash
    .claude/scripts/gh-issue-create.sh bug medium <<'DELIM'` puts the title and
    body on the helper's stdin; the backticks in the body were read as a script
    building itself by substitution.

    **The exemption is the one literal shape and nothing near it**, because
    each widening Copilot found on PR #36 was a way to run the heredoc while
    the helper's name stood in the argv, verified allowed each time:

    * **The word straight after the shell is the helper**, spelled with nothing
      a shell could expand (`HELPER_SCRIPT`). `bash /dev/stdin`,
      `bash /dev/fd/3 3<<EOF` and `bash $X <<EOF` all run the heredoc; and
      `bash X=1 <helper>` takes `X=1` as the script file.
    * **The shell is the run's first word, spelled `bash` or `sh`.** At any
      other position the helper is somebody else's argv —
      `python -c 'exec(sys.stdin.read())' bash <helper> <<EOF` has Python run
      the body. No assignment in front either: `BASH_ENV=/dev/stdin bash
      <helper>` sources the heredoc before the helper starts.
    * **The run is the command's first**, with nothing before it. A function
      or alias defined earlier in the same command replaces the shell —
      `bash() { source /dev/stdin; }; bash <helper> <<EOF` — and a `cd` can
      move what the relative path names. Shell state does not survive between
      Bash tool calls, so the command's own text is the only place such an
      override can be set; what remains is a profile or a planted file, the
      on-disk residual the module docstring names.

    Every refusal this gives up is an over-refusal: `env bash <helper>` and a
    helper run after `&&` are refused as they were before the exemption.
    """
    return (not command[:start].strip()
            and len(words) > 1
            and words[0] in ("bash", "sh")
            and HELPER_SCRIPT.match(words[1]) is not None)


def _consumes_as_script(command, position, ordinary):
    """Whether the script at `position` is executed by its own run or a later
    one in the same pipeline."""
    start, end = _run_bounds(command, position, ordinary)
    words = _run_words(command, start, end, ordinary)
    reads = (not _helper_reads_stdin_as_data(command, start, words)
             and reads_stdin_as_script(words))
    return reads or forwards_to_evaluator(command, position, ordinary)


def pipeline_groups(tokens):
    """`tokens` split into pipelines, each a list of the runs it joins.

    **A pipe is not an adjacency**, and comparing neighbouring runs let an
    intermediate stage carry the bytes past the check:
    `printf 'git p%ssh origin +HEAD:main' u | cat | bash` pairs as
    printf-then-cat and cat-then-bash, and neither pair is a printer feeding a
    shell — while the shell still runs what the printer wrote. Raised in
    review; verified allowed.

    `command_runs` drops the boundary that separated two runs, which is what
    made the distinction unavailable; this keeps it just long enough to say
    whether the runs are in the same pipeline.
    """
    groups, current, run = [], [], []
    for token in tokens:
        if not is_boundary(token):
            run.append(token)
            continue
        if run:
            current.append(run)
            run = []
        if token in ("|", "|&"):
            continue
        if current:
            groups.append(current)
            current = []
    if run:
        current.append(run)
    if current:
        groups.append(current)
    return groups


def unmodelled_printer(tokens):
    """Whether a printer whose OUTPUT this file cannot reproduce feeds a shell.

    **Joining a printer's argv is not the bytes it writes**, and where the two
    differ the join is the safe-looking one. `printf 'git p%ssh origin
    +HEAD:main' u | bash` runs the push; the join is `git p%ssh origin
    +HEAD:main u`, which every check reads as harmless. `echo -e` does the same
    through its escapes. Raised in review; both verified allowed.

    Reproducing `printf` is a specification this file will not carry — the same
    reason it refuses to enumerate git's executing config keys — so the
    unmodellable case refuses instead. The plain forms still go through
    `evaluated_scripts`, which judges the literal text, so `echo 'git status'
    | bash` is unaffected.
    """
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for run in group[:max(shells)]:
            # **A run can be assignments and nothing else**, and `leading_command`
            # answers `""` for one — which `list.index` does not find, so
            # `X=1 | bash` raised `ValueError` out of the hook. A crash is
            # empty stdout, which `PreToolUse` treats as non-blocking: this
            # was a fail-open on a shape a caller can type. Raised in review;
            # verified as a crash.
            command_word = leading_command(run)
            if not command_word:
                continue
            name = program_name(command_word)
            arguments = run[run.index(command_word) + 1:]
            if name == "printf" and any("%" in element for element in arguments):
                return True
            if name == "echo" and any(element.startswith("-") and "e" in element
                                      for element in arguments):
                return True
    return False


def stdin_scripts(command):
    """Every script a shell in `command` is handed on its STDIN.

    **`evaluated_scripts` models one channel by which a shell receives a
    script, and bash has three.** It reads the argv element after `-c`; a shell
    also runs what arrives on stdin, and both spellings of that put the script
    text in the command string where a hook can read it:

        bash <<<'git push origin +HEAD:main'
        bash <<EOF
        git push origin +HEAD:main
        EOF

    Both ran the push and both were admitted — on `main` as well. Found by an
    adversarial audit, which generated 3,696 obfuscations, took the 919 the
    guard allowed, ran each under a shimmed bash and found 431 that executed
    the push.

    **These are not the residual that file's docstring names.** That one is
    `bash script.sh`, a file the hook is not given, and the computed shape
    `sh -c "$(echo …)"`. Here nothing is computed and nothing is on disk: the
    script is a literal word in the argv, exactly as it is in `bash -c '…'` —
    which this guard already refuses. The two halves disagreed, and this is the
    half that was wrong.

    Every other reader of these constructs is left alone, which is what keeps
    `git commit -F - <<EOF` a filing rather than a command: the leading word of
    the run has to be a shell.
    """
    # The bodies first, for `undecodable_heredoc`'s reason: an apostrophe in
    # one used to leave this scan in quote state for everything after it.
    spans = heredoc_spans(command)
    ordinary = [False] * len(command)
    literal = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in spans]):
        ordinary[index] = not in_quotes and not in_comment
        if index == escaped:
            # **An escaped metacharacter is part of the word**, and treating
            # one as a boundary cut the script short: the here-string of
            # `bash <<<git\\ push\\ origin\\ +HEAD:main` yielded `git\\`
            # alone, while the redirection strip removed the whole thing, so
            # nothing downstream saw the push. Raised in review.
            literal[index] = True
            escaped = None
            continue
        if ordinary[index] and command[index] == "\\":
            literal[index] = True
            escaped = index + 1

    def boundary(position):
        return (ordinary[position] and not literal[position]
                and command[position] in METACHARACTERS)

    # **Bodies belong to introducers in ORDER, and `rfind` gave every body the
    # last introducer before it.** In `bash <<A; cat <<B` the first body is
    # `bash`'s, and `rfind` found `<<B`, decided the reader was `cat`, and
    # never judged the script bash runs. Raised in review; verified allowed.
    #
    # `heredoc_spans` yields its spans in opener order and skips an opener that
    # sits inside an earlier body, so the pairing walks both lists together
    # rather than searching backwards from each body.
    openers = [
        match.start() for match in HEREDOC.finditer(command)
        if ordinary[match.start()]
        and not (match.start() > 0 and command[match.start() - 1] == "<")
        and not command.startswith("<<<", match.start())
    ]
    # **Two monotonic cursors, because the containment test that used to sit
    # here was quadratic.** It re-scanned every earlier body for every
    # opener/body pair, so a command carrying enough heredocs ran for long
    # enough to hit the hook timeout — which is empty stdout, which is
    # non-blocking. Raised in review, against the commit that had just removed
    # the same shape from `heredoc_spans` and pinned only that function's
    # timing. An opener inside a body is no longer in this list at all: the
    # scan above is told where the bodies are, so it reports one as quoted.
    cursor = 0
    for start, end, _expands in spans:
        while cursor < len(openers) and openers[cursor] >= start:
            cursor += 1
        if cursor >= len(openers):
            break
        opener = openers[cursor]
        cursor += 1
        if _consumes_as_script(command, opener, ordinary):
            yield command[start:end]

    index = 0
    while index < len(command):
        if not (ordinary[index] and command.startswith("<<<", index)):
            index += 1
            continue
        consumed = _consumes_as_script(command, index, ordinary)
        cursor = index + 3
        while cursor < len(command) and command[cursor] in " \t":
            cursor += 1
        word = word_end(command, cursor, [not literal[position] and value
                                          for position, value
                                          in enumerate(ordinary)])
        if consumed:
            # **A here-string is quote-removed before the shell runs it, and
            # `shlex` has no rule for either dollar quote.** So
            # `bash <<<$'git push origin +HEAD:main'` handed the recursion
            # `$git push …` — a name `program_name` does not match — while bash
            # ran the push. `$'' + BS + BS + 'x67it …'` and the locale spelling did the
            # same. Raised in review; verified allowed.
            #
            # An undecodable one is yielded whole rather than normalised: the
            # recursive judge applies the same fail-closed check to it and
            # refuses with the reason that check states, which keeps one
            # sentence for one decision.
            text = command[cursor:word]
            if undecodable_dollar_quote(text):
                yield text
                index = max(word, index + 3)
                continue
            try:
                parts = shlex.split(strip_dollar_quotes(text), posix=True)
            except ValueError:
                parts = [text]
            if parts:
                yield " ".join(parts)
        index = max(word, index + 3)


def substitution_fed_shells(command):
    """Whether a process substitution supplies a shell in `command` its script.

    **`bash < <(printf '%s\\n' 'git push origin +HEAD:main')` runs the push,
    and every pass here judged the halves apart.** The inner `printf` is data,
    correctly; the redirection strip then removes `< <(…)` whole, correctly,
    because a process substitution IS the redirect target; and what is left is
    a `bash` with no script, which is nothing at all. Raised in review;
    verified allowed, and `bash <(echo …)` runs it too, the substitution being
    a filename the shell is told to execute.

    **Refused rather than read, on `unmodelled_printer`'s argument.** What the
    shell executes is the substitution's OUTPUT, and reproducing a command's
    output is the specification this file declines to carry — the same reason
    `printf 'git p%ssh …' u | bash` refuses instead of being modelled. Reading
    the inner command instead would be right for `<(echo '…')` and wrong for
    every spelling that computes, and the wrong half fails open.

    A run led by a printer is left alone, exactly as the pipeline pass leaves
    one: `echo <(git push origin +HEAD:main)` is text, and the inner command is
    judged in its own right by `substitutions`.
    """
    ordinary = [False] * len(command)
    escaped = None
    bodies = [(start, end) for start, end, _ in heredoc_spans(command)]
    for index, in_quotes, in_comment in shell_positions(command, bodies):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment

    for index in range(len(command) - 1):
        if not (ordinary[index] and ordinary[index + 1]):
            continue
        if command[index:index + 2] not in ("<(", ">("):
            continue
        start, end = _run_bounds(command, index, ordinary)
        if reads_stdin_as_script(_run_words(command, start, end, ordinary)):
            return True
    return False


def evaluated_scripts(tokens):
    """Every token a shell evaluator in `tokens` will execute as a command.

    **`shlex` hands a quoted script back as one data token**, exactly as it does
    a command substitution — so `git log "$(bash -c 'git push origin
    +HEAD:main')"` reached the inner pass as `bash`, `-c` and one opaque string,
    the segment scan found no `git`, and the push ran. Raised in review;
    verified allowed, and the bash behaviour measured with a `git` shim.

    **The data-only boundary applies here too**, and it did not at first: a run
    led by `echo` is text, so `echo bash -c \'git push …\'` was refused for
    quoting a command. Raised in review — the same false-positive class the
    boundary was added to close, left standing in the pass beside it, which is
    this repository\'s most-repeated shape.

    **The bound: a script this hook can READ.** `bash script.sh` runs a file,
    and a hook is handed an argv rather than a filesystem — that is outside what
    any argv guard can see, and it is the same shape as the parameter-expansion
    residual rather than a new one.

    **That bound was stated correctly and applied too widely.** `-c` is one of
    three channels a shell takes a script through, and the other two —
    `bash <<<'…'` and a heredoc — put the text in the command string, where it
    is as readable as the argument to `-c` this function already judges.
    `stdin_scripts` covers them; the file half is what remains outside.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            name = program_name(token)
            if name in EVALUATORS:
                argv = run[index + 1:]
                for position, element in enumerate(argv):
                    if SCRIPT_FLAG.match(element) and position + 1 < len(argv):
                        yield argv[position + 1]
                        break
            elif name == "eval":
                argv = run[index + 1:]
                if argv:
                    yield " ".join(argv)

    # **A shell with no script of its own reads one from the pipe**, and the
    # run before it is where that text is written: `echo 'git push origin
    # +HEAD:main' | bash` ran the push and was admitted. The printer is exempt
    # from the scan by `DATA_ONLY_COMMANDS` — correctly, its arguments are text
    # — but they stop being text the moment a shell is on the other end of the
    # pipe. Found by an adversarial audit.
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for before in group[:max(shells)]:
            command_word = leading_command(before)
            if program_name(command_word) not in DATA_ONLY_COMMANDS:
                continue
            # Sliced past the command WORD rather than past the first token:
            # with an assignment prefix the two differ, and taking `before[1:]`
            # handed the judgement a string beginning `echo`, which the
            # data-only exemption then waved through.
            spoken = before[before.index(command_word) + 1:]
            written = [element for element in spoken
                       if not element.startswith("-")]
            if written:
                yield " ".join(written)


def split_string_payloads(tokens):
    """Every string `env -S` / `--split-string` in `tokens` splits and runs.

    **The payload is a command line held in one word**, the same shape as a
    `bash -c` script: `env -S 'cp /tmp/a .claude/settings.json'` runs `cp`,
    while every scan over words sees `env`, `-S` and one opaque string. Raised
    by Copilot against #26's first round, where the verb scan claimed `env`
    coverage it only had for the unsplit form — and the same string hid a
    `git push` from the push grammar all along.

    `-S` may be bundled after option letters that take no value (`-iS`), its
    string glued or the next word, and GNU accepts `--s` for the long form.
    `-u` and `-C` take a value, so an `S` after either is that value. The scan
    stops at the first word that is neither an option nor an assignment,
    because that word is the command env runs and its options are its own.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            if program_name(token) != "env":
                continue
            argv = run[index + 1:]
            position = 0
            while position < len(argv):
                element = argv[position]
                position += 1
                if element == "--":
                    break
                if element.startswith("--"):
                    name, equals, value = element.partition("=")
                    if len(name) > 2 and "--split-string".startswith(name):
                        if equals:
                            yield value
                        elif position < len(argv):
                            yield argv[position]
                        break
                    if not equals and name in ("--unset", "--chdir"):
                        position += 1
                    continue
                if element.startswith("-") and len(element) > 1:
                    letters = element[1:]
                    for offset, letter in enumerate(letters):
                        rest = letters[offset + 1:]
                        if letter == "S":
                            if rest:
                                yield rest
                            elif position < len(argv):
                                yield argv[position]
                            break
                        if letter in "uC":
                            if not rest:
                                position += 1
                            break
                    else:
                        continue
                    if letter == "S":
                        break
                    continue
                if ASSIGNMENT.match(element):
                    continue
                break


# Commands whose arguments are text and never a command line.
#
# **An allow-list, and the direction is load-bearing.** A name missing from
# here costs an over-refusal; the converse — listing the wrappers that DO
# execute their arguments — fails open on the first one nobody thought of, and
# `timeout`, `env`, `nohup`, `xargs`, `sudo`, `command` and `time` all run
# `git push origin +HEAD:main` perfectly well. A run led by anything not named
# here keeps reaching the scan.
DATA_ONLY_COMMANDS = {"echo", "printf", ":", "true", "false"}


# `shlex(punctuation_chars=True)` emits a maximal RUN of these as ONE token, so
# an operator can arrive glued to its neighbour and match no separator by name.
PUNCTUATION = set("();<>|&")

# What bash treats as a word separator when unquoted. **Not the same set as
# PUNCTUATION**, which is `shlex`'s: this one carries the whitespace, because
# the question it answers is where a WORD begins rather than where a token
# does.
METACHARACTERS = set("|&;()<> \t\n")

# What ends a command RUN. A subset of METACHARACTERS: a redirection
# operator and a space separate words within one run rather than ending
# it. **Read only through `_ends_run`**, because the `&` here is also the
# second character of `>&` and `<&` and the first of `&>` (#37).
RUN_SEPARATORS = set(";&|()\n")


def is_boundary(token):
    """Whether `token` ends the command run it appears in.

    **`);` is one token, and it matched nothing.** So
    `git log -1; (echo ok);git push origin +HEAD:main` left the push inside a
    run still led by `echo`, the data-only exemption skipped it, and bash ran
    it — measured with a `git` shim. Raised in review.

    A token made entirely of shell punctuation is a boundary whatever it is
    glued into, which also settles `<(`: a process substitution is executed
    BEFORE the command it is an argument to, so the `git` inside one belongs to
    no printer's run. `echo <(git push origin +HEAD:main)` ran the push too,
    measured the same way, and both are one question about where a run ends.
    """
    return token in SEPARATORS or (
        token != "" and all(char in PUNCTUATION for char in token))


def command_runs(tokens):
    """`tokens` split into the separate commands the shell would run."""
    current = []
    for token in tokens:
        if is_boundary(token):
            if current:
                yield current
            current = []
        else:
            current.append(token)
    if current:
        yield current


# Commands that only READ a file named in their arguments, so a writing verb's
# name there is a file being inspected and not a verb being run. Anything else in
# the leading position — `bash`, `sh`, `env`, `command`, `xargs`, `source` —
# is judged, because the wrapper list is the one that fails open.
#
# **Only programs that cannot run a command.** `awk` was here and its
# `system()` and `cmd | getline` run a shell; GNU `sed` runs one with `e`; and
# `less` runs one with `!`. Each is removed, so a run they lead is judged like
# any other. Raised by Copilot.
READING_COMMANDS = frozenset({
    "cat", "diff", "file", "git", "grep", "head", "ls", "rg", "stat", "tail",
    "wc",
})

# **The only programs a substitution may run, and it is an allow-list.** A
# substitution runs before the approved command holding it is judged, so what
# runs inside one decides before any grant does. The first form listed the
# programs whose argument is code — `awk`, `sed`, the interpreters — and a
# branch-controlled executable walked around it: `ls "$(./tools/run)"` names
# nothing on a list and runs whatever the branch put there, `gh` and the
# ledger included. Raised by Copilot, twice. So only programs whose effects
# are modelled or harmless run here, each by bare name — a word with a `/` is
# a file somebody chose, whatever it is called — and everything else,
# `awk` and `sed` among it, is refused. `git` is on the list because every
# `git` word is judged by the rest of this file.
SUBSTITUTION_PROGRAMS = frozenset({
    "[", "basename", "cat", "cut", "date", "dirname", "echo", "expr", "false",
    "git", "grep", "head", "hostname", "id", "jq", "ls", "printf", "pwd",
    "readlink", "realpath", "rg", "seq", "sort", "stat", "tail", "test", "tr",
    "true", "uname", "wc", "whoami",
})
# `uniq` was here and takes `INPUT OUTPUT`: its second operand is written (#297).


# What runs the word after it as a script or a program, so a computed word in
# that position is a program nobody can name from the source.
LAUNCHERS = frozenset({
    ".", "bash", "command", "dash", "env", "exec", "ksh", "nohup", "sh",
    "source", "time", "xargs", "zsh",
})


class BraceExplosion(Exception):
    """A brace expansion with more words than this guard will build (#362)."""


# The most words one brace expansion may produce. `{,}` thirty times is 2^30
# strings from a hundred characters, and a hook that times out is non-blocking,
# so the expansion stops here and the command is refused instead.
BRACE_WORD_LIMIT = 256


def brace_alternatives(word):
    """Every word a comma brace expansion in `word` produces, `word` if none.

    Raises `BraceExplosion` past `BRACE_WORD_LIMIT` words; `decision` answers
    that with a refusal.
    """
    pending, done = [word], []
    while pending:
        current = pending.pop()
        match = re.search(r"\{([^{}]*,[^{}]*)\}", current)
        if match is None:
            done.append(current)
            continue
        choices = match.group(1).split(",")
        if len(pending) + len(done) + len(choices) > BRACE_WORD_LIMIT:
            raise BraceExplosion(word)
        # Reversed, so the words come out in the order bash writes them.
        pending.extend(
            current[:match.start()] + choice + current[match.end():]
            for choice in reversed(choices))
    return done


BRACE_RANGE = re.compile(
    r"(-?\d+|[A-Za-z])\.\.(-?\d+|[A-Za-z])(?:\.\.(-?\d+))?")


def _brace_group(text):
    """The first brace group in `text` that bash expands, as `(start, end,
    values)`, or `None`. A group is read with its braces balanced, so
    `{a,{b,c}}` is one group of two alternatives; one with no top-level comma
    and no range inside is literal to bash and is looked through for groups
    nested in it (`{a{b,c}}` is `{ab}` and `{ac}`).

    A backslash is not an escape here. The words arrive from `shlex` with their
    quoting already removed, so `a\\{b,c}` reaches this as `a\{b,c}` while
    bash still expands it; reading `\{` as escaped would pass what bash runs.
    Treating every brace as live over-expands (`\{a,b\}` is judged as two
    words) and a word judged too often is refused, never allowed."""
    index = 0
    while index < len(text):
        if text[index] != "{":
            index += 1
            continue
        depth, cursor, commas = 1, index + 1, []
        while cursor < len(text) and depth:
            char = text[cursor]
            if char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
            elif char == "," and depth == 1:
                commas.append(cursor)
            cursor += 1
        if depth:
            index += 1
            continue
        end = cursor
        inner = text[index + 1:end - 1]
        if commas:
            cuts = [index] + commas + [end - 1]
            return index, end, [text[a + 1:b] for a, b in zip(cuts, cuts[1:])]
        spec = BRACE_RANGE.fullmatch(inner)
        if spec:
            first, last, step = spec.groups()
            numeric = [first.lstrip("-").isdigit(), last.lstrip("-").isdigit()]
            if numeric[0] == numeric[1]:
                step = abs(int(step or 1)) or 1
                if numeric[0]:
                    low, high, to_text = int(first), int(last), str
                else:
                    low, high, to_text = ord(first), ord(last), chr
                # Counted before any value is built: `{1..999999999}` is a
                # billion strings if the cap is checked after them.
                if abs(high - low) // step + 1 > BRACE_WORD_LIMIT:
                    raise BraceExplosion(text)
                direction = step if low <= high else -step
                return index, end, [to_text(n) for n in range(
                    low, high + (1 if direction > 0 else -1), direction)]
        index += 1
    return None


def brace_variants(word):
    """Every word `word` becomes under brace expansion: comma lists and
    `{a..e}`, `{1..3}` and `{1..9..2}` ranges, balanced and nested, expanded
    until no group is left (#360). Bash makes each of them a separate word.

    More than `BRACE_WORD_LIMIT` words raises `BraceExplosion`.
    """
    pending, out = [word], []
    while pending:
        current = pending.pop()
        group = _brace_group(current)
        if group is None:
            out.append(current)
            continue
        start, end, values = group
        if len(out) + len(pending) + len(values) > BRACE_WORD_LIMIT:
            raise BraceExplosion(word)
        pending.extend(
            current[:start] + value + current[end:]
            for value in reversed(values))
    return out


def computed_word(token):
    """Whether `token` is a word bash computes, so its value is not in the source.

    **A literal comparison misses what bash expands before it runs.** A word
    carrying `$` or a backtick has a value that is not in the source at all. A
    range, a nested brace or an extglob is computed too, rather than expanded
    here: `x-{l..l}.sh` is `x-l.sh`, and the comma-only expansion cannot see
    it. Modelling each form is the enumeration that missed these, so any `{`
    left unmatched, and any extglob opener, is reported as computed. Raised by
    Copilot.
    """
    return bool("$" in token or "`" in token
                or re.search(r"(?<!\$)\{", token)
                or re.search(r"[@+!?*]\(", token))


def launched(run, index):
    """Whether `run[index]` is in a position where it is RUN as a program."""
    if ASSIGNMENT.match(run[index]):
        return False
    if run[index] == leading_command(run):
        return True
    # A `-c` script is a script, not a program name, and `evaluated_scripts`
    # has already judged what it runs.
    if index > 0 and SCRIPT_FLAG.match(run[index - 1]):
        return False
    previous = index - 1
    while previous >= 0 and (run[previous].startswith("-")
                             or ASSIGNMENT.match(run[previous])):
        previous -= 1
    return previous >= 0 and program_name(run[previous]) in LAUNCHERS


def token_followed_by_group(tokens, run, index):
    """Whether `run[index]` is immediately followed by a `(` in `tokens`.

    `command_runs` drops the boundary, so the flat token list is searched for
    this run's position — the last token of `run` only, which is the only one
    a `(` can follow without ending the run first.
    """
    if index != len(run) - 1:
        return False
    for position in range(len(tokens) - 1):
        if tokens[position] is run[index] and tokens[position + 1].startswith("("):
            return True
    return False


def computed_program_offence(tokens):
    """The reason to refuse a program word bash computes, or `None`."""
    for run in command_runs(tokens):
        if program_name(leading_command(run)) in READING_COMMANDS:
            continue
        for index, token in enumerate(run):
            # The tokeniser splits an extglob at its parenthesis, so
            # `run-@(a).sh` arrives as `run-@` with the group in a separate
            # run. A word ending in an extglob operator is computed.
            computed = computed_word(token) or (
                token[-1:] in ("@", "+", "!", "?", "*")
                and token_followed_by_group(tokens, run, index))
            if computed and launched(run, index):
                return (
                    f"`{token}` is run as a program whose name bash computes "
                    "— a variable, a substitution, a pattern or a brace "
                    "expansion — so this guard cannot read which program "
                    "runs. Refusing rather than judging the word instead of "
                    "the program it opens."
                )
    return None


def program_flag_offence(tokens):
    """The reason to refuse a flag that makes a listed program run one, or `None`.

    `rg --pre CMD` runs CMD on every searched file, which makes `rg`, listed as a
    reader and as a substitution program, arbitrary execution (#278, #279). Only a
    run where the program is launched counts, so `echo rg --pre` is text. The
    scan sits on the token list every substitution body is judged through too.
    """
    for run in command_runs(tokens):
        for index, token in enumerate(run):
            flags = EXECUTING_PROGRAM_FLAGS.get(program_name(token))
            # Not `launched()`: a launcher's option argument (`xargs -n 1 rg`,
            # `env -C d rg`) or a launcher missing from LAUNCHERS (`sudo`,
            # `timeout 5`, `nice`) leaves the program unrecognised by position.
            # The program name anywhere in a run that is not data-only is
            # judged, over-refusing `grep rg --pre`, which is the safe direction.
            if (flags is None or ASSIGNMENT.match(token)
                    or program_name(leading_command(run)) in DATA_ONLY_COMMANDS):
                continue
            for word in run[index + 1:]:
                if word == "--":
                    break  # what follows is a pattern or a path, never a flag
                name = word.split("=", 1)[0]
                if any(
                        name == flag
                        or (not flag.startswith("--") and word.startswith("-")
                            and not word.startswith("--") and flag[1] in word[1:])
                        or (flag.startswith("--") and name.startswith("--")
                            and len(name) > 2 and flag.startswith(name))
                        for flag in flags):
                    return (
                        f"`{program_name(token)} {word}` makes it run a program "
                        "the command line names, on every file it reads; "
                        "nothing here needs that, and `rg` is listed as a "
                        "program that cannot run one."
                    )
    return None


def env_removes_variables(words):
    """Whether an `env` in `words` clears or unsets variables before its program.

    Only `env`'s OWN options are read, from the `env` word to the first word
    that is neither an option nor an assignment, so the program's arguments
    (`env git log -S foo`, `env git diff -u`) are not mistaken for them. A short
    cluster holding `u` or `i` counts (`-iu`), and so does any abbreviation of
    `--unset`, `--ignore-environment` and `--split-string` that GNU getopt
    accepts (`--ign`, `--uns`). `-C` and `-a` take the next word as their value,
    a bare `-` is `-i`, and `--` ends the options.
    """
    # A run LED by git or rg: an `env` in it is an argument (`rg env -i src`,
    # `git log --grep env -S foo`), not an invocation. Any other run reads every
    # `env`, since which git word is the program cannot be told from the words
    # alone (`env -a git nice env -u HOME rg x .` takes `git` as a value).
    if words and program_name(words[0]) in ("git", "rg"):
        return False
    for start, env_word in enumerate(words):
        if program_name(env_word) != "env":
            continue
        skip = False
        for offset, word in enumerate(words[start + 1:], start + 1):
            if skip:
                skip = False
                continue
            if word == "--":
                # The end of env's options; what follows may be another `env`, and
                # a later one is still reached by the outer loop.
                if (offset + 1 < len(words)
                        and program_name(words[offset + 1]) == "env"):
                    nested = env_removes_variables(words[offset + 1:])
                    if nested:
                        return nested
                break
            if word == "-":
                return True  # a bare `-` is `--ignore-environment`
            if not word.startswith("-"):
                if ASSIGNMENT.match(word):
                    continue
                # The program: env's options are over. A program that is itself
                # `env` (`env -a git env -u HOME rg`, where `git` was a value) is
                # read from there, whatever git or rg word came before it.
                if program_name(word) == "env":
                    nested = env_removes_variables(words[offset:])
                    if nested:
                        return nested
                break
            if word.startswith("--"):
                name = word.split("=", 1)[0]
                if len(name) > 2 and "--split-string".startswith(name):
                    return "split"
                if len(name) > 2 and any(
                        long.startswith(name)
                        for long in ("--unset", "--ignore-environment")):
                    return True
                # Options that take a value, abbreviated as getopt allows.
                # The signal options take an optional `=value` only and never the
                # next word, so they are not listed.
                skip = "=" not in word and len(name) > 2 and any(
                    long.startswith(name) for long in ("--chdir", "--argv0"))
            else:
                # A short cluster, walked letter by letter: a letter that takes a
                # value ends it, the value being the rest of the word or the next.
                for position, letter in enumerate(word[1:], start=1):
                    if letter == "S":
                        return "split"
                    if letter in "ui":
                        return True
                    if letter in "CaP":
                        skip = position == len(word) - 1
                        break
    return False


def git_environment_offence(tokens):
    """The reason to refuse a git environment variable that executes, or `None`.

    `GIT_EXTERNAL_DIFF=./x git diff` and `GIT_CONFIG_COUNT=1
    GIT_CONFIG_KEY_0=core.sshCommand ...` are the spellings of `--ext-diff` and
    `git -c` that arrive as assignment words, which `git_segments` never reads
    (#281). Judged where a word is an assignment to the environment: the leading
    assignments of a run, and the arguments of `env`, `export` and their kind.
    """
    for run in command_runs(tokens):
        # Whether a word here is an assignment to the environment: at the start
        # of a run, right after a launcher, and from `env` or `export` onward,
        # wherever in the run they stand (`sudo env GIT_PAGER=x git log`).
        assigning, sticky = True, False
        # The generic names (`PATH`, `HOME`, `PAGER`) are ordinary for any other
        # program, so they are judged only in a run that names git or rg. A
        # script that runs git underneath is the gap #298 tracks.
        # An assignment that persists (`export PATH=x`, `PATH=x;` with no
        # command, `env`/`declare`) reaches a later run's git, so it is judged
        # whatever else the run names; `BASH_ENV`, `ENV` and `SHELL` are acted
        # on by the shell itself and are refused everywhere.
        words = [word for word in run if not ASSIGNMENT.match(word)]
        runs_git = any(program_name(word) in ("git", "rg") for word in words)
        mentions = (runs_git or not words
                    or any(program_name(word) in ASSIGNING_COMMANDS for word in words))
        lead = program_name(words[0]) if words else ""
        if lead in (ASSIGNING_COMMANDS - {"env"}) | {"read", "mapfile", "readarray"} or (
                lead == "printf" and "-v" in words):
            # A name set without an assignment word: `export GIT_PAGER` after an
            # earlier assignment, `read GIT_PAGER`, `printf -v GIT_PAGER`.
            for word in words[1:]:
                if (not word.startswith("-")
                        and GIT_EXECUTING_ENVIRONMENT.match(word.split("=", 1)[0] + "=")):
                    return ENVIRONMENT_REFUSAL.format(name=word.split("=", 1)[0])
        # `env -S 'cmd'` hands env a command string whose own options this guard
        # does not read, with git inside the one word, so the text of every word is
        # searched. A string with no git or rg in it (`env -S 'cp a b'`) is judged
        # by its content as before.
        removal = env_removes_variables(words)
        names_git = runs_git or (bool(removal) and any(
            re.search(r"\b(?:git|rg)\b", word) for word in words))
        if names_git and removal:
            # Removing a variable changes which configuration git reads.
            return ENVIRONMENT_REFUSAL.format(name="env -u")
        for word in run:
            if ASSIGNMENT.match(word):
                name = re.split(r"[\[+=]", word, maxsplit=1)[0].upper()
                if (assigning or sticky) and runs_git and (
                        name not in INERT_ENVIRONMENT and not name.startswith("LC_")):
                    # An allow-list in a run that names git or rg: the denylist
                    # below grows a name per review round (#298).
                    return ENVIRONMENT_REFUSAL.format(name=name)
                if ((assigning or sticky) and GIT_EXECUTING_ENVIRONMENT.match(word)
                        and (mentions
                             or word.upper().startswith(
                                 ("GIT_", "RIPGREP_", "BASH_ENV=", "ENV=", "SHELL=")))):
                    return ENVIRONMENT_REFUSAL.format(name=word.split("=", 1)[0])
                continue
            if word.startswith("-"):
                continue  # an option of env, sudo or a launcher
            name = program_name(word)
            # `sudo` is sticky like `env`: its options take arguments of their own
            # (`sudo -u x env A=1 git`), so a word after one cannot be told from
            # the command by position alone.
            sticky = sticky or name in ASSIGNING_COMMANDS or name == "sudo"
            assigning = name in ASSIGNING_COMMANDS or name in LAUNCHERS or name == "sudo"
    return None


# `git config` flags that only READ. A write is anything else: `git config
# alias.p push` makes `p origin HEAD:main` a push whose verb is `p`, and several
# other keys (`core.pager`, `core.sshCommand`, `core.hooksPath`) are executed.
# Allow-listed because the writing forms keep gaining spellings (`--add`,
# `--unset`, `--edit`, `set`), the way `-c`'s keys do (#294).
CONFIG_READ_FLAGS = frozenset({
    "--get", "--get-all", "--get-regexp", "--get-urlmatch", "--list", "-l",
    "--show-origin", "--show-scope", "--name-only", "--null", "-z",
    "--local", "--global", "--system", "--worktree", "--includes",
    "--no-includes", "--default", "--type", "--bool", "--int", "--path",
})
CONFIG_READ_ACTIONS = frozenset({
    "--get", "--get-all", "--get-regexp", "--get-urlmatch", "--list", "-l",
})

DASHED_GIT = re.compile(r"^git-[a-z][a-z0-9-]*$")

# Leads whose operands are names or patterns, never a program they run. `git`
# is here because every git word is judged by the rest of this file, and its
# operands include branch names (`git checkout -b chore/git-aliases`).
INERT_OPERAND_LEADS = READING_COMMANDS | DATA_ONLY_COMMANDS | frozenset({
    "basename", "cd", "chmod", "cp", "cut", "dirname", "du", "jq", "ln",
    "mkdir", "mv", "pwd", "readlink", "realpath", "rm", "tee", "test", "touch",
    "tr", "type", "which", "[",
})
# `npm exec`, `npm run` and `npx` run a program, `gh` aliases and extensions
# and `dotnet exec` run one, and `sort --compress-program` runs one: those
# leads are inert only for the subcommands that take a name as data.
INERT_SUBCOMMANDS = {
    "npm": frozenset({
        "add", "ci", "i", "info", "install", "remove", "uninstall", "view",
    }),
    "gh": frozenset({
        "api", "auth", "issue", "label", "pr", "release", "repo", "run",
        "search", "status", "workflow",
    }),
    "dotnet": frozenset({
        "build", "format", "pack", "publish", "restore", "test",
    }),
}


VALUELESS_OPTIONS = frozenset({
    "--global", "--ignore-scripts", "--no-audit", "--no-color", "--no-fund",
    "--quiet", "--save-dev", "--save-exact", "--silent", "--verbose", "-D",
    "-E", "-g", "-q",
})


def inert_operands(run, lead):
    """Whether `run`'s lead only reads its operands as data."""
    raw = leading_command(run)
    if not raw:
        # A run of assignments alone has no command word, so it runs nothing.
        return True
    # A word with a `/` is a file somebody chose, whatever it is called; `git`
    # alone is exempt because every git word is judged by the rest of this file.
    # `program_name` splits on a backslash too, so `'.\ls'` is a file as well.
    if re.search(r"[\\/]", raw) and lead != "git":
        return False
    position = run.index(raw) + 1
    if lead in INERT_OPERAND_LEADS:
        return True
    options = []
    while position < len(run) and run[position].startswith("-"):
        options.append(run[position])
        position += 1
    if lead in INERT_SUBCOMMANDS:
        # An option the lead gives a value to (`npm --prefix install exec ...`)
        # would make that value the "subcommand", so only options known to take
        # none may stand before it.
        return (all(option in VALUELESS_OPTIONS for option in options)
                and position < len(run)
                and run[position] in INERT_SUBCOMMANDS[lead])
    # `command -v git-lfs` looks a name up and runs nothing; `find` runs a
    # program only through one of its exec actions.
    if lead == "command":
        return any(re.fullmatch(r"-[pVv]*[Vv][pVv]*", option)
                   for option in options)
    return lead == "find" and not any(
        word in ("-exec", "-execdir", "-ok", "-okdir") for word in run)


def git_config_offence(segment):
    """The reason to refuse a `git config` that is not a plain read, or `None`."""
    stripped = after_global_options(segment)
    if not stripped or stripped[0] != "config":
        return None
    flags = [word for word in stripped[1:] if word.startswith("-")]
    positional = [word for word in stripped[1:] if not word.startswith("-")]
    if all(flag in CONFIG_READ_FLAGS for flag in flags) and (
            any(flag in CONFIG_READ_ACTIONS for flag in flags)
            # `git config user.name` names one dotted key and no value: a read.
            # A bare word is a verb (`edit`, `set`, `unset`), which is not.
            or (len(positional) == 1 and "." in positional[0])):
        return None
    return (
        "`git config` is refused unless it only reads (`--get`, `--get-all`, "
        "`--get-regexp`, `--list`): a written `alias.*` rewrites a verb the "
        "protected-ref checks read by name, and `core.pager`, `core.sshCommand` "
        "and `core.hooksPath` are executed. It is `git -c` made persistent, "
        "and that is refused too."
    )


def dashed_git_offence(tokens):
    """The reason to refuse a `git-<verb>` executable, or `None`.

    `git-send-pack origin HEAD:main` and `git-push` name the program, not the
    subcommand, so `git_segments` never reads them as a git invocation and no
    push check runs (#294). Nothing here runs one. A script (`git-trailer-pr.sh`)
    carries its extension and is not an executable git dispatches to.
    """
    for run in command_runs(tokens):
        lead = program_name(leading_command(run))
        if lead in DATA_ONLY_COMMANDS:
            continue
        # Only a word that is RUN is judged, so `git checkout -b chore/git-x`,
        # `git add git-hooks` and `npm i git-url-parse` are operands. A wrapper's
        # whole run is judged, since `timeout 5 git-push` and `sudo -u x git-push`
        # put the program after a word `launched()` does not read as a launcher.
        # The subcommands of git that run a program, with a command string the
        # scan cannot read (`bisect run`, `submodule foreach`, `rebase -x`), are
        # refused by `git_subcommand_offence` instead.
        # Inverted (#297): a lead is judged by what it runs, so the whole run is
        # read unless the lead is known to treat its operands as data. A lead
        # missing from `WRAPPERS` no longer fails open.
        if lead in ("command", "find") and inert_operands(run, lead):
            candidates = run[:1]
        elif inert_operands(run, lead):
            candidates = [token for index, token in enumerate(run)
                          if launched(run, index)]
        else:
            candidates = run
        for token in candidates:
            # A quoted command string (`'git-push origin HEAD:main'`) names the
            # program in its first word.
            word = token.split(None, 1)
            if (word and not ASSIGNMENT.match(token)
                    and DASHED_GIT.match(program_name(word[0]))):
                return (
                    f"`{token}` is a git subcommand run as its own program, so "
                    "the push, force and delete checks that read `git <verb>` "
                    "never see it. Run it as `git <verb>`."
                )
    return None


def git_segments(tokens):
    """Yield the argv slice of every `git` invocation in a compound command.

    **A `git` token is only an invocation where a command can stand.**
    `echo git push origin +HEAD:main` was refused, and a guard that refuses
    honest traffic is the one this file's own docstring says somebody turns
    off. Raised in review.

    The test is the run's LEADING word, not where `git` sits inside it, because
    a wrapper puts the real command in the middle — which is why the scan still
    covers the whole run.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            if program_name(token) != "git":
                continue
            # **Bash expands braces before git sees a word**, so
            # `--{oneline,output=/tmp/y}` is two options, `--grep {x,--output=y}`
            # a value and an option, and `git {log,push}` a subcommand with an
            # operand. Every check reads the words git will receive, not the
            # words typed (#360).
            yield [word for typed in run[index + 1:]
                   for word in brace_variants(typed)]


def after_global_options(segment):
    """`segment` from its subcommand onward, with git's global options dropped."""
    index = 0
    while index < len(segment) and segment[index].startswith("-"):
        index += 2 if segment[index] in GLOBAL_VALUE_FLAGS else 1
    return segment[index:]


def global_options(segment):
    """`segment`'s leading global options — everything before the subcommand.

    The position is the whole point: `-c` before the subcommand is git's
    configuration option, and `-c` after `commit` is "reuse this commit's
    message". Refusing the second would break an ordinary commit, so the two
    are told apart the way git tells them apart — by where they stand.
    """
    stripped = after_global_options(segment)
    if not stripped:
        return segment
    return segment[:len(segment) - len(stripped)]


def subcommand_of(segment):
    stripped = after_global_options(segment)
    return stripped[0] if stripped else ""


def git_subcommand_offence(segment):
    """The reason to refuse a git subcommand this guard cannot read, or None.

    **The push allow-list and the forbidden-flag scan both key off the
    subcommand word.** `F=push; git $F origin +HEAD:main` tokenises `$F`, so
    `push_offence` never sees `push`, and `Bash(git log:*)` auto-approves the
    compound that builds it. The residual `docs/harness-boundaries.md` names
    is an argument after a literal verb — `git log $F` — not the verb itself.
    Raised by Copilot.
    """
    subcommand = subcommand_of(segment)
    if verb_name(subcommand) in GIT_EXECUTING_SUBCOMMANDS:
        return (
            f"`git {subcommand}` runs the program its configuration or `-x` "
            "names, which a branch can supply; nothing here uses it. Use "
            "`git diff` and read the output."
        )
    arguments = after_global_options(segment)[1:]
    # By `verb_name`, as the executing subcommands above are, so a case or quote
    # variant is the same verb. A short cluster holding `x` (`-ix`) and any
    # `--e...` abbreviation of `--exec` count for `rebase`; over-refusing
    # `--empty` is the safe direction.
    verb = verb_name(subcommand)
    if verb in ("apply", "am"):
        return (
            f"`git {subcommand}` writes the files its patch names, which are in "
            "the patch and not on the command line, so no path can be judged "
            "(#297). Nothing here applies a patch."
        )
    if ((verb == "bisect" and "run" in arguments)
            or (verb in ("submodule", "submodule--helper") and "foreach" in arguments)
            or (verb == "filter-branch")
            or (verb == "rebase" and any(
                re.match(r"^-[A-Za-z]*x", argument) or argument.startswith("--e")
                for argument in arguments))):
        return (
            f"`git {subcommand}` here runs a command string the command line "
            "supplies (`bisect run`, `submodule foreach`, `filter-branch`, "
            "`rebase -x`), which this guard cannot read as argv and nothing "
            "here uses. Run the command itself."
        )
    if (plumbing := plumbing_verb(segment)) is not None:
        return (
            f"`git {plumbing}` updates a remote ref without the verb `push`, "
            "so the protected-branch, force and delete checks never read it. "
            "Use `git push <remote> <branch>`, which they do."
        )
    if not subcommand or SAFE_SUBCOMMAND.match(subcommand):
        return None
    return (
        f"`git {subcommand}` names its subcommand with an expansion, a "
        "substitution or a pattern this guard cannot read, so the push "
        "allow-list never sees the argv bash executes. Prefix grants match "
        "the typed string; refusing rather than judging the word instead of "
        "the verb it becomes (docs/harness-boundaries.md)."
    )


def push_offence(segment):
    """The reason to refuse a `git push`, or None — by ALLOW-list.

    `push` is LOCATED rather than assumed to be first, so no global option,
    known or not, can hide it: that was `-C`, and then `--attr-source` in the
    fix for `-C`.
    """
    # `push` is the subcommand when everything before it is either an option or
    # an option's value — and a value is recognised STRUCTURALLY, as a non-flag
    # immediately preceded by a flag, rather than by consulting a list of
    # value-taking globals. That is what makes an unknown global harmless:
    # `git --attr-source HEAD push` and `git --some-future-global X push` both
    # resolve, without this file knowing either flag.
    #
    # It also keeps `git log push` — a ref that happens to be called `push` —
    # out of the push checks, because `log` is a non-flag that no flag precedes,
    # so `log` is the subcommand and `push` is one of its arguments. Refusing
    # that was the one false positive the allow-list introduced, and trading it
    # away would have been the wrong direction: a guard that fires on innocent
    # traffic is one somebody turns off.
    start = None
    for index, element in enumerate(segment):
        if element.startswith("-"):
            continue
        if verb_name(element) == "push":
            start = index
            break
        if index == 0 or not segment[index - 1].startswith("-"):
            break  # this is the subcommand, and it is not `push`
    if start is None:
        return None
    rest = segment[start + 1:]

    for element in rest:
        if element.startswith("-") and element not in PUSH_ALLOWED_FLAGS:
            return (
                f"`git push {element}` is not one of the options this guard "
                "recognises. A push is admitted only when every part of it is "
                "known — one remote, one refspec naming a destination, and "
                "options from a fixed set. Refusing what is unrecognised is "
                "what stops the next spelling nobody listed."
            )

    positional = [a for a in rest if not a.startswith("-")]
    if len(positional) != 2:
        return (
            "a push must name a remote and exactly one refspec. "
            "`git push origin` and `git push origin HEAD` name no destination, "
            "so neither can be shown not to be a protected branch — a hook is "
            "given no repository state to resolve them against."
        )

    remote, refspec = positional
    if not SAFE_REMOTE.match(remote):
        return f"`{remote}` is not a plain remote name"

    if refspec.startswith("+"):
        return "a `+` refspec is a force push — the spelling that carries no `--force`"
    if ":" in refspec:
        source, _, destination = refspec.partition(":")
        if not source:
            return "a `:branch` refspec deletes the remote branch"
    else:
        source, destination = refspec, refspec
    # git matches an unqualified destination against the remote's refs by its
    # rev-parse rules, one of which is `refs/<name>`, so `heads/main` is
    # `refs/heads/main`. Both prefixes come off, in that order, before any
    # comparison (#275).
    for prefix in ("refs/", "heads/"):
        if destination.startswith(prefix):
            destination = destination[len(prefix):]
    if source in UNRESOLVABLE_SOURCES and destination == source:
        return (
            f"`{source}` names no destination of its own; it updates whatever "
            "branch you are standing on, which a hook cannot resolve"
        )
    if not SAFE_REF.match(destination):
        return (
            f"`{destination}` is not a plain branch name. A wildcard or pattern "
            "destination can include a protected branch while equalling none — "
            "`refs/heads/*:refs/heads/*` is the case that made this an "
            "allow-list."
        )
    if destination in PROTECTED_BRANCHES:
        return (
            f"pushing to `{destination}` is a decision, not a step, in every "
            "spelling of the refspec"
        )
    return None


# Substitutions and evaluators both recurse, and a crafted nest of either would
# otherwise reach Python's own limit — where the hook dies with a traceback
# rather than a verdict, which is the one direction a guard must not fail in.
MAX_NESTING = 24


def offence(command, depth=0, judged=None):
    """The reason to refuse `command`, or None to allow it.

    **`judged` is a verdict cache, and it is what keeps the cost finite.** Each
    of the four readings and each extracted substitution recurses onto a string
    barely shorter than the one it came from, so a command nesting them
    multiplies: `$( echo ${a:-{z,X}} )` repeated seven times is 155 characters
    and took over sixty seconds — past the hook timeout, which produces no
    verdict, which `PreToolUse` treats as non-blocking. Fail-open by
    exhaustion, on an innocent command, and a regression from the commit that
    added the readings. Found by an adversarial audit.

    **The cache holds the verdict rather than the visit**, which is the part
    that has to be right: remembering only that a string had been seen would
    return None the second time a refusing string appeared, and lose the
    refusal. A string reached inside its own evaluation is recorded as None
    first, so a cycle terminates without inventing a verdict — the outer call
    is the one that answers.
    """
    if judged is None:
        judged = {}
    if command in judged:
        return judged[command]
    judged[command] = None

    verdict = _offence(command, depth, judged)
    judged[command] = verdict
    return verdict


def _offence(command, depth, judged):
    """`offence`'s body, called only through its cache."""
    if depth > MAX_NESTING:
        return (
            "this command nests shells or substitutions more deeply than the "
            "guard will follow; refusing rather than reading part of it."
        )

    if undecodable_heredoc(command):
        return (
            "a heredoc names a delimiter this guard cannot decode, so it "
            "cannot tell where the body ends or which lines after it are "
            "commands; refusing rather than reading part of it."
        )
    # Judged as typed and with its line continuations removed, which bash does
    # first: `a=\<newline>( [ 1<<EOF]=1 )` is a compound assignment only once the
    # pair is gone, and either reading refusing is enough.
    spellings = (command, join_continuations(command))
    if any(ambiguous_expansion(text) for text in spellings):
        return (
            "a `#`, a `<<` or a command substitution inside `${...}`: bash "
            "reads it as text where a comment or heredoc reading would strip the "
            "lines after it, and this guard cannot tell which without parsing "
            "the expansion. Write it outside the braces."
        )
    if any(ambiguous_arithmetic(text) for text in spellings):
        return (
            "`((` and `<<` in one command: inside arithmetic `<<` is a shift "
            "and opens no heredoc, outside it hides every line to its "
            "delimiter, and this guard cannot tell which without parsing "
            "bash's arithmetic. Run the arithmetic and the heredoc as "
            "separate commands."
        )


    # **What bash does when a substitution prints nothing, judged beside what
    # it does when one prints something.** The words around an empty
    # substitution join, so `git $( )push origin +HEAD:main` is a push — and
    # the tokeniser saw `(` and `)` as run boundaries instead. Both readings
    # have to be safe, and only one of them is the string that was typed.
    # **An expansion has more than one reading, and the command is safe only if
    # it is safe under all of them.** Empty joins the words around it,
    # whitespace splits one into several, a default puts its own text on the
    # line, and a single-element brace range is the text inside it. Each is
    # what bash does in the shell these commands run in — no positional
    # parameters, no variables set — so none of these is the run-time residual
    # `docs/harness-boundaries.md` names; the dangerous string is in the source
    # in every case.
    for description, reading in (
        ("a command substitution taken as empty", without_substitutions),
        ("an expansion taken as whitespace", splitting_expansions),
        ("an expansion taken as its default", defaulted_expansions),
        ("a brace expansion taken as one word", brace_expanded),
    ):
        variant = outside_verbatim(command, reading)
        if variant != command:
            refusal = offence(variant, depth + 1, judged)
            if refusal is not None:
                return f"with {description}: {refusal}"

    if any(contains_gh_word(body) or runs_unmodelled_program(body)
           for body in process_substitution_bodies(strip_heredocs(command))):
        return (
            "a process substitution runs `gh` or a program outside the short "
            "list whose effects this guard models, and it executes before "
            "the command holding it is checked against its grant. Run it as "
            "its own command."
        )

    if substitution_fed_shells(command):
        return (
            "a shell is handed its script by a process substitution, so what "
            "it runs is that command's output rather than anything written "
            "here; refusing rather than judging the source instead of the "
            "result."
        )

    for script in stdin_scripts(command):
        # **A substitution inside a script a shell will run supplies the
        # command itself**, and no reading here models that:
        # `bash <<<"$(printf git) push origin +HEAD:main"` runs the push, while
        # the inner `printf git` is judged as the data it is and the
        # empty-substitution reading leaves a bare `push …`. The same answer
        # `unmodelled_printer` gives, for the same reason — the text that
        # decides is not in the source. Raised in review; verified allowed.
        if substitutions(script):
            return (
                "a script handed to a shell on stdin builds part of itself "
                "with a command substitution, so what that shell runs cannot "
                "be read; refusing rather than judging the source instead of "
                "the result."
            )
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"in a script handed to a shell on stdin: {refusal}"

    for text, quotes in expandable_regions(command):
        # **The continuation join has to happen before anything looks for a
        # substitution, not only before the tokeniser.** Bash removes
        # `\<newline>` inside double quotes too, so
        # `git log "$\<newline>(git push origin +HEAD:main)"` becomes a live
        # `$(` — and this scan, running on the raw text, saw no opener while
        # `shlex` later returned the whole quoted value as data. Raised in
        # review; verified allowed. Only for a command-line region: a heredoc
        # body arrives with `quotes` false and is not a command line.
        text = join_continuations(text, quotes=quotes)
        for inner in substitutions(text, quotes=quotes):
            # The body is judged in its own right first, so a refusal that
            # names what it found — a shell evaluator, a push — is the one
            # reported; the allow-list below then refuses what that admits.
            refusal = offence(inner, depth + 1, judged)
            if refusal is not None:
                return f"inside a command substitution: {refusal}"
            refusal = substituted_gh_offence(inner)
            if refusal is not None:
                return refusal

    # Stripped once, and used by BOTH paths below. The fallback used to scan the
    # raw `command`, which put the heredoc false positive straight back: a body
    # that mentions a forbidden flag would be refused on the raw string the
    # moment anything else in the line failed to tokenise. A body is data on
    # every path, not only on the one that parses — and so is a comment, which
    # is why `strip_comments` runs here rather than being left to the lexer.
    #
    # `strip_redirections` is outermost because it is the only one of the four
    # that wants the others' work done first: a redirection inside a heredoc
    # body or a comment is not one bash performs, and there is nothing left of
    # either by the time it runs.
    # `join_continuations` sits after `strip_comments` because a backslash at
    # the end of a COMMENT continues nothing — bash ends a comment at the
    # newline — so joining first would have swallowed the next line into it.
    performed = separate_lines(
        join_continuations(strip_comments(strip_heredocs(command))))

    # **Judged on the string the strip is about to read, and that is the whole
    # placement argument.** A `>` inside a heredoc body or a comment is not a
    # redirection bash performs, so judging the raw command would refuse a
    # commit message describing this very change — the mistake
    # `unreadable_dollar_quote` records making one line down. And judging after
    # the strip is impossible: the strip is what removes the targets.
    #
    # Every reading above recurses through `offence`, so a target assembled by
    # an expansion is judged under each of them too.
    refusal = redirection_offence(performed)
    if refusal is not None:
        return refusal

    resolved = strip_redirections(performed)

    # **The check and the code that acts on it must read the SAME string**, and
    # putting this on the raw command was wrong twice over. It refused a
    # heredoc body or a comment that merely mentions `$'\n'` — data on every
    # path, which is the invariant the rest of this pipeline is built on, and
    # it made a commit message describing this very change unwritable. And it
    # missed `git $\<newline>'\x70ush' origin +HEAD:main`, where the sigil and
    # its quote are separated by a continuation: nothing was there to refuse on
    # the raw string, while `strip_dollar_quotes` — running after the join —
    # found the quote and un-sigilled it, leaving `shlex` a literal
    # `\x70ush` that is not `push`. Both raised in an adversarial audit; the
    # bypass was live on `main` too, the over-refusal was this branch's own.
    unreadable = unreadable_dollar_quote(resolved)
    if unreadable is not None:
        return unreadable

    # **On the string with the redirections gone**, because a redirection's
    # target is not an operand of the verb in front of it — `cp a b 2>
    # .claude/x` has already been judged above as the redirection it is — and
    # before the tokeniser, because the quoting a destination is judged by is
    # what `shlex` is about to remove (#26).
    refusal = writing_verb_offence(resolved)
    if refusal is not None:
        return refusal

    # `strip_dollar_quotes` turns `$'…'` and `$"…"` into the ordinary quoting
    # `shlex` resolves, on the string just checked.
    stripped = strip_dollar_quotes(resolved)
    try:
        lexer = shlex.shlex(stripped, posix=True, punctuation_chars=True)
        # Comments are already gone, and `shlex` would take a second, wider view
        # of them: its `commenters` fires mid-word, where bash's fires only at
        # the start of one. Left on, `--grep=#x ; git push origin +HEAD:main`
        # lost the push to the lexer.
        lexer.commenters = ""
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        # **Unparseable is not hostile.** The first version refused anything it
        # could not tokenise, reasoning that bash would fail too — false about
        # the parser in use, and it refused an ordinary commit. A parse failure
        # DEGRADES to the substring scan the settings deny already performs:
        # never weaker than the status quo, never a silent pass.
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in stripped:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the raw string, which is the weaker "
                    "check the settings deny already performs."
                )
        # **The push allow-list has to reach this path too, and it did not.**
        # The fallback scanned for forbidden flags and `ext::` alone, so any
        # command this guard cannot tokenise had the push grammar switched off
        # entirely — and a line is easy to make untokenisable on purpose. An
        # audit reached it through `$'\''`, whose escaped quote left `shlex`
        # with no closing quotation; the push then sat in plain text and was
        # admitted. Here the check can only be the crude one, which is the
        # point of the path.
        if re.search(r"\bgit\b[^;&|\n]*\bpush\b", stripped):
            return (
                "a `git push` appears in a command this guard could not "
                "tokenise, so its remote and refspec cannot be read; refusing "
                "rather than admitting a push nothing checked."
            )
        return None

    if unmodelled_printer(tokens):
        return (
            "a printer whose output this guard cannot reproduce writes into a "
            "shell, so what that shell runs cannot be read; refusing rather "
            "than judging the arguments instead of the bytes."
        )

    for script in evaluated_scripts(tokens):
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"inside a shell evaluator: {refusal}"

    for payload in split_string_payloads(tokens):
        # `env -S` undoes its own escapes and expands `${NAME}` before it
        # splits, so a payload carrying either is not the argv it runs.
        if "\\" in payload or "$" in payload:
            return (
                "`env -S` is handed a string carrying a backslash or a `$`, "
                "which env rewrites before splitting it into the command it "
                "runs; refusing rather than judging the text instead of that "
                "argv (#26)."
            )
        # Judged behind its `env`, so an option in the payload (`-C dir`) is
        # still env's and moves the directory a relative target lands in (#359).
        refusal = offence("env " + payload, depth + 1, judged)
        if refusal is not None:
            return f"inside `env -S`: {refusal}"

    refusal = computed_program_offence(tokens)
    if refusal is not None:
        return refusal

    refusal = (program_flag_offence(tokens) or git_environment_offence(tokens)
               or dashed_git_offence(tokens))
    if refusal is not None:
        return refusal

    for segment in git_segments(tokens):
        refusal = git_subcommand_offence(segment) or git_config_offence(segment)
        if refusal is not None:
            return refusal
        refusal = push_offence(segment)
        if refusal is not None:
            return refusal

        for element in global_options(segment):
            # `-cdiff.external=<cmd>` was raised in review as a compact form
            # git accepts. **It does not**, on 2.45.1: `unknown option`, and
            # the usage line spells the option `-c <name>=<value>`. So this is
            # hardening rather than a fix, and it is cheap because the global
            # option set is small and fixed — no git subcommand flag can reach
            # here, since this loop only ever sees the tokens BEFORE the
            # subcommand. `-C` is left alone, and the comparison is
            # case-sensitive for exactly that reason.
            if element in CONFIG_OPTIONS or element.startswith("-c") or any(
                    element.startswith(option + "=") for option in CONFIG_OPTIONS):
                return (
                    "`git -c` / `--config-env` sets configuration for one "
                    "invocation, and git EXECUTES several config keys — "
                    "`alias.*`, `core.pager`, `core.sshCommand`, "
                    "`core.hooksPath` and more. Nothing here passes one, so "
                    "the option is refused rather than its value guessed at."
                )

        subcommand = subcommand_of(segment)
        value_flags = VALUE_FLAGS_BY_SUBCOMMAND.get(subcommand, frozenset())
        skip = False
        for element in segment:
            if skip:
                skip = False
                continue
            if element in value_flags:
                skip = True
                continue
            # **git accepts any unambiguous ABBREVIATION of a long option**,
            # so a canonical-prefix test reads less than it looks like it does.
            # Measured against a real remote: `--upload-p=<cmd>` and even
            # `--upl=<cmd>` are accepted by `git fetch` and the command RUNS;
            # only `--u` is refused, and for being ambiguous rather than
            # unknown. Raised in review.
            #
            # So the test runs both ways — the element starting with a
            # forbidden flag, and a forbidden flag starting with the element.
            # An abbreviation of something harmless that happens to prefix one
            # of these is refused too; that is over-refusal, which is the
            # direction to be wrong in, and `--u` was never going to work.
            name = element.split("=", 1)[0]
            abbreviation = name.startswith("--") and len(name) > 2
            for flag in FORBIDDEN_FLAGS:
                if element.startswith(flag) or (
                        abbreviation and flag.startswith(name)):
                    return (
                        f"`git ... {flag}` is refused: it reaches outside the "
                        "repository this grant was for — writing, executing a "
                        "configured command (--ext-diff, --textconv), or in "
                        "--no-index's case reading a path git would not "
                        "otherwise open — and the settings deny it matches only "
                        "the unquoted spelling. This hook compares the resolved "
                        "argv, and any unambiguous abbreviation of it."
                    )
            if subcommand not in REPOSITORY_SUBCOMMANDS:
                continue
            for substring in FORBIDDEN_SUBSTRINGS:
                if substring in element:
                    return (
                        f"`{substring}` is a git transport that runs its "
                        "argument as a command, and no Bash permission rule can "
                        "express it."
                    )
    return None


# **Every scan here must finish inside the hook's timeout, because a hook that
# times out is non-blocking.** `_closing_brace` and `_closing_paren` each
# re-read the rest of the command per opener, so their work is openers times
# length, bounded by `SCAN_BUDGET`; the scanners as a whole grow faster than
# linearly in length alone, bounded by `LENGTH_BUDGET`. A command past either
# is refused before any scan runs, which fails closed where the timeout would
# fail open.
SCAN_BUDGET = 2_000_000
LENGTH_BUDGET = 100_000


def scan_budget(command):
    """The refusal for a command the scanners could not finish in time."""
    openers = command.count("(") + command.count("${")
    if len(command) <= LENGTH_BUDGET and openers * len(command) <= SCAN_BUDGET:
        return None
    return (
        f"this command's {len(command)} characters and {openers} brackets or "
        "parameter expansions are more than this guard can judge inside its "
        "time limit, and a guard that times out admits the command, so it is "
        "refused instead. Shorten it or split it into separate commands."
    )


def decision(event):
    """The hook's answer for one event: `None` to allow, or the deny payload.

    **Split out of `main` so a caller can ask for a verdict without paying an
    interpreter to answer it (#46).** The harness asks about a thousand times a
    run, and a process per answer was the largest single spawn cost in the
    suite. What is left in `main` is the stdin/stdout half and nothing else.

    **The split is safe only while the two halves agree, so that is a case
    rather than a claim.** `TheGitArgvGuard.test_the_spawned_hook_answers_what
    _the_import_does` drives the same commands down both paths and compares the
    payloads. Without it the suite would be reading a function no session calls:
    delete `main`'s `json.dump` and every in-process assertion still passes
    while the hook refuses nothing where it is actually wired.
    """
    if event.get("tool_name") != "Bash":
        return None

    command = (event.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return None

    global EVENT_CWD
    cwd = event.get("cwd")
    EVENT_CWD = cwd if isinstance(cwd, str) and cwd else None

    try:
        reason = scan_budget(command) or offence(command)
    except BraceExplosion:
        reason = (
            f"a brace expansion in this command makes more than "
            f"{BRACE_WORD_LIMIT} words, more than this guard can judge inside "
            "its time limit, and a guard that times out admits the command, so "
            "it is refused instead. Write the words out or split the command "
            "(#362, docs/harness-boundaries.md)."
        )
    except Exception:  # noqa: BLE001 - the direction is the point
        # **A crash is empty stdout, and `PreToolUse` reads empty stdout as
        # non-blocking**, so every defect in this file has been a fail-open.
        # Four have been found by review and audit — a `ValueError` out of
        # `list.index`, two out of `str.index`, and one recursion — and each
        # admitted whatever the command was.
        #
        # **This is not the malformed-event case above and the two answers
        # differ on purpose.** An unreadable event says nothing about any
        # command, so refusing there would stop the session for a defect in
        # this file; a crash while judging THIS command says this command
        # broke the parser, and refusing one command is proportionate and
        # tells the caller exactly that.
        traceback.print_exc(file=sys.stderr)
        reason = (
            "this command crashed the guard that judges it, so nothing about "
            "it has been established; refusing rather than admitting what "
            "could not be read. The traceback is on stderr."
        )
    if reason is None:
        return None

    return {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }


# Seconds before the watchdog answers for a run that has not finished. The hook
# entry in `.claude/settings.json` sets no timeout, and a hook that times out
# is non-blocking, so a run still going at this point is refused here.
WATCHDOG_SECONDS = 20


# Whoever answers first holds this lock and records it, so the watchdog and the
# main path cannot both write: two JSON documents on stdout are not an answer.
ANSWER_LOCK = threading.Lock()
ANSWERED = []


def refuse_after(seconds, answer):
    """Start a timer that writes `answer` and ends the process when it fires,
    and return it so a run that finishes can cancel it (#362).

    A thread, because `signal.alarm` is not on Windows. It fires between
    bytecodes, so it answers for a Python loop that runs long; a single
    regular-expression match holds the interpreter and is bounded by
    `SCAN_BUDGET` and `LENGTH_BUDGET` instead.
    """
    def fire():
        with ANSWER_LOCK:
            if ANSWERED:
                return
            ANSWERED.append(True)
            json.dump(answer, sys.stdout)
            sys.stdout.flush()
            os._exit(0)

    timer = threading.Timer(seconds, fire)
    timer.daemon = True
    timer.start()
    return timer


def main():
    del ANSWERED[:]
    try:
        # **Bytes, decoded as UTF-8, and never the locale's code page** (#363).
        # `json.load(sys.stdin)` decodes with cp1252 on Windows, where a
        # command holding a character such as `Á` (the bytes C3 81) raises,
        # and the handler below allows an event it could not read: every check
        # in this file skipped for a command anyone can write. `replace` keeps
        # a byte that is not UTF-8 judged as text, not dropped.
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace"))
    except (json.JSONDecodeError, ValueError):
        # A hook that cannot read its own input has established nothing. Say so
        # and allow: refusing every Bash call on a malformed event would take
        # the session down for a defect in this file.
        print("guard-git-argv: unreadable hook event; not judging", file=sys.stderr)
        return 0

    timer = refuse_after(WATCHDOG_SECONDS, {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": (
                f"this guard had not finished judging the command after "
                f"{WATCHDOG_SECONDS} seconds, and a hook that times out is "
                "non-blocking, so the command is refused instead. Shorten it "
                "or split it into separate commands (#362, "
                "docs/harness-boundaries.md)."
            ),
        }
    })
    payload = decision(event)
    timer.cancel()
    with ANSWER_LOCK:
        # If the watchdog got here first it holds the lock until the process
        # ends, so this line is reached only when it has not answered.
        if not ANSWERED:
            ANSWERED.append(True)
            if payload is not None:
                json.dump(payload, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
