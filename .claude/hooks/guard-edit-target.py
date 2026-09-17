#!/usr/bin/env python3
"""Judge an edit target by the file it resolves to, not by the path it spells.

**A permission rule matches a SPELLING and an edit lands on a FILE**, and the
gap between those two is #181. `.claude/settings.json` denies
`Edit(.claude/scripts/**)`, and `/review-grok`'s frontmatter denies
`Edit(.claude/**)`, `Edit(.github/**)`, `Edit(deploy/**)` and `Edit(.git/**)`
beside the `src/`, `tests/` and `docs/` the command exists to write. Every one
of those is compared against the path a caller typed. A symbolic link — or, on
Windows, a junction — inside an allowed tree is a spelling no deny matches
while the write lands wherever the link points: inside a denied tree, or out of
the checkout altogether.

**What stood there before was a premise rather than a check.** The helper suite
fails on any tracked mode `120000` in `git ls-files -s`, so `main` carries no
tracked link on any push, and an invocation whose only writers are `Write` and
`Edit` cannot add one. Both halves are true and neither covers the case that
matters: `/review-grok` runs over the *branch* under review, locally, before CI
has said anything about it, and a branch is what introduces files. The premise
is a statement about `main`; the exposure is on the branch.

**So the rule here is one predicate and it names no tree: an edit target must
be the file its path spells.** Resolve the target, re-anchor it on the resolved
checkout root, and refuse it if the two disagree. That refuses a link into the
machinery trees, a link out of the checkout, and every future deny the same
way — this file holds no copy of any deny list, so it cannot go stale as one
changes, and it cannot lock the repository out of its own control surface
either: an edit spelled at the file it actually is passes here and is then
judged by the rules that already exist.

**Three things follow from the anchoring that are worth stating before someone
reads a false positive as a bug.** The checkout root is itself resolved, so a
worktree under a temp root that is a link — `/tmp` on macOS, an 8.3 or `subst`
path on Windows — is judged against its own real spelling rather than refused
wholesale. An anchor is a checkout **root** and every anchor containing the
target must agree, because an anchor excuses one link traversal — its own root
prefix — so an anchor at a linked directory inside the tree excuses precisely
what this file refuses. And the comparison folds case where the **filesystem**
does, asked of the filesystem by `case_insensitive` rather than read off the
platform: Windows' `realpath` returns the on-disk case, so `DOCS/x` would
otherwise differ from `docs/x` and be refused for a difference that is not one
— and macOS mounts APFS case-insensitively by default, where a platform test
folds nothing and a differently-cased checkout prefix matches no anchor at
all, which is the branch that admits. A case-insensitive mount on Linux is the
same case again, which is why the question is asked of the mount.

**The out-of-tree half is an allow-list, and it used to be the residual.** A
path this file cannot place under any anchor and that also resolves outside
every one of them was admitted, on the argument that refusing would take the
session's memory and scratchpad with it. That was true and the conclusion did
not follow: **the exception was argued against the wrong threat**, as though
the alternative were refusing everything, when it could instead name the roots
it exists for. `/review-branch` holds an unrestricted `Write` and consumes
untrusted branch text, `/ship` runs it unattended, and a prompt-injected diff
can name an absolute path — a shell profile, an SSH key, a credential — which
the fallback then allowed (#21). So `scratch_roots` names the two roots the old
paragraph named, the platform temp directory the scratchpad is created under
and the user's `~/.claude`, and everything else is refused. Within `~/.claude`
the harness's own control surface is refused too: that root is admitted for
STATE and a credential or a settings file is not state. A spelling no anchor
recognises that nevertheless *lands inside* a checkout is refused by the
paragraph below instead, whatever alphabet it is in, because the matcher then
judged a string that is not this file. Nothing in the exposure this closes can
spell either: `/review-grok`'s site contract admits one plain
repository-relative path per row, with no leading slash, no drive letter and no
`..` segment, and the adjudicator drops a row that is not.

**What that leaves, stated because an allow-list is not an absence of
residual.** The temp root is admitted whole rather than the session's own
subdirectory within it, because no environment variable names the scratchpad
and deriving it would be a guess this file cannot check. So a write into
another process's temp file is still admitted, which is a smaller surface than
the home directory it replaces and not an empty one.

**And one whole grammar is refused rather than judged**, which is the exception
to "this file holds no list": on Windows a spelling beginning `\\` — the
extended-length prefix, the device prefix, a UNC share — skips the very
normalisation a permission rule's matcher depends on, so the matcher never sees
the target at all. Measured, both forms wrote into a denied directory. The one
exemption is an anchor in that same grammar containing the target, which is a
repository genuinely on a network share.

Protocol: PreToolUse, matcher `Edit|Write|NotebookEdit|MultiEdit`. Exit 0 and
print nothing to allow; print the deny JSON to refuse. The JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed — `guard-git-argv.py` argues the same
choice and this file follows it.
"""

import json
import os
import sys
import tempfile
import unicodedata

# The tools that write a file. `MultiEdit` is listed although this repository's
# harness does not surface it: the matcher in `.claude/settings.json` is a
# regular expression over the tool name, so a tool that is registered later
# arrives here judged rather than unjudged, and a name this file does not know
# is refused by the `EDITING_TOOLS` test below only after the matcher has
# already let it through.
EDITING_TOOLS = ("Edit", "Write", "NotebookEdit", "MultiEdit")

# Where each of those carries its target. `Edit` and `Write` use `file_path`;
# `NotebookEdit` uses `notebook_path`. Both are read, and a matched call
# carrying neither is refused rather than waved through — a write whose target
# this file cannot see is one it has established nothing about.
PATH_KEYS = ("file_path", "notebook_path")

# **Windows names the same file in more than one alphabet, and a permission
# rule reads only one of them.** Every spelling beginning `\\` is the other
# one: the extended-length prefix `\\?\` and the device prefix `\\.\`, which
# exist precisely to SKIP the normalisation a matcher depends on, and the UNC
# form `\\server\share\...`, which reaches the local disk through the
# administrative shares. **Both were measured in this checkout with
# `.claude/sandbox/**` denied, and both were CREATED**: a `Write` to
# `\\?\C:\dev\ashamray\.claude\sandbox\probe-unc.txt` and one to
# `\\localhost\C$\dev\ashamray\.claude\sandbox\probe-share.txt`. The plain
# spelling of either file is refused. Both probe files were deleted.
#
# So the whole family is refused rather than the two prefixes that were found
# first — enumerating spellings is the deny-list shape this repository has
# rejected twice, and the UNC form is what a list of prefixes would have
# missed. Refused rather than resolved, because a hook can only allow or deny:
# it cannot hand the matcher the plain spelling it would have judged.
#
# **Unless a checkout is itself named that way**, which is the one legitimate
# case — a repository on a network share. Then the anchors are `\\`-spelled
# too, the matcher's strings and the guard's agree, and nothing here fires.
# Scoped to Windows because no other platform has a second alphabet: `//x` on
# POSIX is an ordinary path, and refusing it would be a rule about nothing.
def alternate_alphabet(path):
    """Whether `path` is spelled in Windows' non-drive path grammar."""
    return os.name == "nt" and path[:2].replace("/", "\\") == "\\\\"


def case_insensitive(path):
    """Whether `path`'s filesystem resolves a differently-cased spelling to it.

    **`os.path.normcase` folds case on Windows and nowhere else**, and that is
    a statement about the PLATFORM where what matters is the FILESYSTEM. macOS
    mounts APFS case-insensitively by default, so `/Users/x/Repo` and
    `/users/x/repo` are one directory there while `normcase` leaves them
    different strings — and a comparison built on it decides the target is
    under no anchor at all, which is the branch that admits. Raised by Copilot;
    the same is true of a case-insensitive mount on Linux.

    Asked of the filesystem rather than read off `sys.platform`: a component of
    the path is case-flipped and both spellings are `stat`ed, and one file with
    one device and inode under two spellings is the answer.

    **Which component is not a detail, and picking the basename alone was a
    gap.** A checkout at `/Users/me/123` has nothing to flip in its last
    component, so the probe fell to the platform default — `False` on macOS,
    where the mount folds — and a linked target spelled `/users/me/123/...`
    matched no anchor and fell through unjudged. Raised by Copilot. Every
    component is tried, deepest first, and the first one that changes under
    `swapcase` carries the probe; a path with no cased component anywhere is
    the only case left to the platform default, and it cannot arise under a
    root that holds a `.git`.
    """
    normalised = os.path.normpath(path)
    parts = normalised.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        flipped = parts[index].swapcase()
        if flipped == parts[index]:
            continue
        other = os.sep.join(parts[:index] + [flipped] + parts[index + 1:])
        try:
            here = os.stat(normalised)
            there = os.stat(other)
        except OSError:
            return False
        return (here.st_dev, here.st_ino) == (there.st_dev, there.st_ino)
    return os.name == "nt"


def form_insensitive(path):
    """Whether the filesystem resolves NFC and NFD spellings to one file.

    **The twin of `case_insensitive`, and it exists because composing
    unconditionally was wrong.** This file argued that composing "can never
    make two paths look like one" — and on a normalisation-SENSITIVE
    filesystem, ext4 among them, `/tmp/caf\u00e9` and its decomposed sibling are
    two directories that can coexist. Folding them into one key let a link
    resolving into the sibling compare equal to a path inside the checkout, so
    the argument for skipping the probe was the bypass. Raised by Copilot.

    Asked the same way as case, and for the same reason. Where no component has
    a distinct alternate form the answer is `False` **by construction rather
    than as a fallback**: a path that re-normalises to itself has no
    differently-normalised spelling to be confused with.
    """
    normalised = os.path.normpath(path)
    parts = normalised.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        part = parts[index]
        other = unicodedata.normalize(
            "NFD" if unicodedata.is_normalized("NFC", part) else "NFC", part)
        if other == part:
            continue
        candidate = os.sep.join(parts[:index] + [other] + parts[index + 1:])
        try:
            here = os.stat(normalised)
            there = os.stat(candidate)
        except OSError:
            return False
        return (here.st_dev, here.st_ino) == (there.st_dev, there.st_ino)
    return False


def traits_of(path):
    """What this path's filesystem treats as one name: (case, normalisation)."""
    return (case_insensitive(path), form_insensitive(path))


def key(path, traits):
    """One comparable spelling of `path`, under its filesystem's equivalences.

    **Both halves are asked of the mount rather than assumed**, and each was a
    bypass in one direction before it was: folding nothing on a
    case-insensitive APFS volume let a differently-cased prefix match no anchor
    at all, and folding normalisation everywhere let two coexisting names on
    ext4 collapse into one. A comparison is only as good as the equivalence the
    filesystem actually holds.
    """
    folded, composed = traits
    spelling = os.path.normcase(os.path.normpath(path))
    if composed:
        spelling = unicodedata.normalize("NFC", spelling)
    return spelling.lower() if folded else spelling


def same(left, right, traits):
    """Whether two absolute paths name the same place."""
    return key(left, traits) == key(right, traits)


def under(child, parent, traits):
    """Whether `child` is `parent` or sits beneath it, lexically."""
    child = key(child, traits)
    parent = key(parent, traits)
    if child == parent:
        return True
    if not parent.endswith(os.sep):
        parent += os.sep
    return child.startswith(parent)


# **The control surface, wherever a `.claude` directory holds it.** This
# repository denies `Edit(.claude/scripts/**)`, `hooks/**`, `commands/**`,
# `agents/**`, `sandbox/**` and both settings files, and the argument in
# `docs/harness-boundaries.md` is that a session able to rewrite one of those
# before invoking it makes every fixed endpoint in the chain a fiction. The
# user's own `~/.claude` has the same shape and grants strictly more — its
# settings and hooks apply to every project — so the same names are refused
# there. `.credentials.json` is beside them because the fallback below exists
# for the harness's STATE writes and a credential is not one.
CONTROL_DIRECTORIES = frozenset({
    "agents", "commands", "hooks", "plugins", "sandbox", "scripts", "skills",
})
CONTROL_FILES = frozenset({
    ".credentials.json", "AGENTS.md", "CLAUDE.md", "settings.json", "settings.local.json",
})

# **Folded copies, because comparing a lowered name against an unlowered set is
# a hole rather than a nicety.** `CLAUDE.md` lowers to `claude.md`, which is in
# neither set above, so the one entry whose spelling is not already lower case
# was admitted on a case-insensitive filesystem — every Windows and default
# macOS host. Caught by the case written for it.
CONTROL_DIRECTORIES_FOLDED = frozenset(
    name.lower() for name in CONTROL_DIRECTORIES)
CONTROL_FILES_FOLDED = frozenset(name.lower() for name in CONTROL_FILES)


def scratch_roots():
    """The roots a write outside every checkout is permitted to land in.

    **The exception this bounds was argued against the wrong threat.** The
    docstring above states it as "refusing would take the session's memory and
    scratchpad with it", which is true, and reads as though the alternative
    were refusing everything. It is not: the exception can name the roots it
    exists for. `/review-branch` holds an unrestricted `Write` and consumes
    untrusted branch text, and `/ship` runs it unattended — so a prompt-injected
    diff could steer an absolute path at a shell profile, an SSH key or a
    credential, and the fallback returned allow (#21).

    Two roots, which are the two the docstring already names. The session
    scratchpad is created under the platform temp directory, so that directory
    is asked for rather than guessed at.

    **`tempfile.gettempdir()` and nothing beside it, which was not the first
    form.** That form also admitted `TMPDIR`, `TEMP` and `TMP` from the
    environment, on the reasoning that a host may set any of them — and
    `gettempdir()` already applies exactly that precedence, so reading them
    again added nothing except every stale one. A leftover `TEMP=$HOME` beside
    a live `TMPDIR=/tmp` made the whole home directory a scratch root and
    `~/.ssh` writable, which is the case this allow-list exists to refuse.
    Raised by Copilot against the commit that introduced it.

    **Each is kept as (spelled, resolved), for the reason `anchors` keeps its
    own pairs and one platform makes unmissable.** macOS reaches the temp
    directory through a link — `TMPDIR` is under `/var/folders`, whose real
    path is `/private/var/folders` — so a scratch write compared against the
    spelled root alone has a resolution that is not under it, and the session's
    own scratchpad would be refused on every macOS host. The `harness` job runs
    this suite on three platforms, which is the only reason that is knowable
    from here.
    """
    # **The narrower root first.** The first matching root answers, and a
    # host whose HOME sits under the temp directory (`/tmp/home`) put
    # `~/.claude/settings.json` inside the temp root, which admitted it before
    # the control-surface exclusion was ever asked. Raised by Copilot.
    roots = [
        harness_state_root(),
        tempfile.gettempdir(),
    ]
    return [(os.path.abspath(root), os.path.realpath(root))
            for root in roots if root]


def harness_state_root():
    """`~/.claude`, spelled once for `scratch_roots` and `outside_offence`."""
    return os.path.abspath(os.path.join(os.path.expanduser("~"), ".claude"))


def control_surface(path, root, traits):
    """Which control-surface name `path` holds beneath `root`, or `None`.

    Judged on the components between the two, so a `settings.json` in a
    project's own tree is not this file's business and one at
    `~/.claude/settings.json` is.
    """
    try:
        relative = os.path.relpath(path, root)
    except ValueError:
        return None
    parts = [part for part in relative.replace("\\", "/").split("/")
             if part not in ("", ".")]
    folded, _ = traits
    directories = CONTROL_DIRECTORIES_FOLDED if folded else CONTROL_DIRECTORIES
    files = CONTROL_FILES_FOLDED if folded else CONTROL_FILES
    for part in parts[:-1]:
        if (part.lower() if folded else part) in directories:
            return part
    if parts:
        last = parts[-1].lower() if folded else parts[-1]
        if last in files or last in directories:
            return parts[-1]
    return None


def outside_offence(spelled, lexical, resolved):
    """The reason to refuse a target that lands outside every checkout.

    **Turns the fallback from "admit unless it lands in a checkout it cannot
    place" into "admit only what it is for"**, which is the direction every
    other rule in this repository already goes.

    Both the spelling and the resolution have to qualify, and each against
    either spelling of the root. A path under a temp root that resolves out of
    it is the link traversal this whole file is about, arriving at the one
    place the anchors do not reach.
    """
    for spelled_root, real_root in scratch_roots():
        traits = traits_of(spelled_root)

        def within(path):
            return (under(path, spelled_root, traits)
                    or under(path, real_root, traits))

        if not (within(lexical) and within(resolved)):
            continue
        # **The control-surface exclusion is the harness-state root's, not the
        # temp root's.** `control_surface` reads `scripts`, `commands` and
        # `settings.json` as protected at any depth, which is right under
        # `~/.claude` and refused a scratch `/tmp/session/scripts/note.md`
        # under the temp root. Raised by Copilot.
        #
        # **What the temp root refuses instead is a checkout's machinery.**
        # Admitting it whole, the first repair, made `.claude/` writable in
        # every checkout the temp root holds — and the sweeps' detached
        # worktrees live exactly there. So a target inside a checkout under
        # this root is judged against the protected inventory, relative to
        # that checkout, and a plain scratch file is admitted.
        #
        # **Both the spelling and the resolution are judged, each where it
        # lands.** A link under the temp root whose target is
        # `~/.claude/settings.json` — reachable when HOME itself is under the
        # temp root — was never judged by the harness-state root, because the
        # spelling is not under it, and then this branch admitted it as
        # scratch. The same link could land in another temp checkout's
        # machinery. Raised by Copilot.
        if not same(spelled_root, harness_state_root(), traits):
            state = harness_state_root()
            state_traits = traits_of(state)
            for state_root in (state, os.path.realpath(state)):
                if under(resolved, state_root, state_traits):
                    named = control_surface(resolved, state_root, state_traits)
                    if named is not None:
                        return (
                            f"guard-edit-target: {spelled} resolves to "
                            f"{resolved}, inside the harness's own control "
                            f"surface — `{named}` — through the temp root "
                            "(docs/harness-boundaries.md)."
                        )
                    return None
            # **A home directory inside the temp root is not scratch.** With
            # HOME at `/tmp/home`, `~/.ssh/authorized_keys` sat under the temp
            # root and was admitted — the arbitrary home write #21 closed.
            # Only when home is BENEATH the temp root: on Windows the temp root
            # is usually beneath home instead, and excluding home there would
            # refuse the scratchpad itself. `~/.claude` is judged just above,
            # as state. Raised by Copilot.
            home = os.path.abspath(os.path.expanduser("~"))
            for home_root in {home, os.path.realpath(home)}:
                if (within(home_root)
                        and not same(home_root, spelled_root, traits)
                        and (under(lexical, home_root, traits)
                             or under(resolved, home_root, traits))):
                    return (
                        f"guard-edit-target: {spelled} resolves to {resolved}, "
                        "inside the home directory, which sits under the temp "
                        "root here and is not scratch (#21, "
                        "docs/harness-boundaries.md)."
                    )
            for path in (lexical, resolved):
                checkout = checkout_root(path)
                if checkout is None or not within(checkout):
                    continue
                machinery = protected_in_worktree(path, checkout)
                if machinery is not None:
                    return (
                        f"guard-edit-target: {spelled} targets {machinery} in "
                        "a checkout under the temp root, where none of this "
                        "session's permission rules apply "
                        "(docs/harness-boundaries.md)."
                    )
            return None
        base = real_root if under(resolved, real_root, traits) else spelled_root
        named = control_surface(resolved, base, traits)
        if named is None:
            return None
        return (
            f"guard-edit-target: {spelled} resolves to {resolved}, inside the "
            f"harness's own control surface — `{named}` grants what every "
            "other rule here bounds, and this exception exists for the "
            "session's state writes rather than for its configuration (#21, "
            "docs/harness-boundaries.md)."
        )
    return (
        f"guard-edit-target: {spelled} resolves to {resolved}, which is "
        "outside every checkout here and outside the scratch and harness-state "
        "roots this guard admits. An unattended command reading untrusted "
        "branch text can name an absolute path, so a write nothing can place "
        "is refused rather than admitted (#21, docs/harness-boundaries.md)."
    )


def checkout_root(path):
    """The nearest ancestor of `path` holding a `.git`, or `None`.

    **An anchor has to be a checkout ROOT rather than any directory the session
    happens to stand in**, and the difference is a bypass rather than a
    nicety. An anchor excuses exactly one link traversal — the one on its own
    root prefix — so an anchor at `<checkout>/docs/tree`, where `tree` links
    into `.claude/scripts`, excuses precisely the traversal this file exists
    to refuse: the target's spelling is `docs/tree/helper.sh`, its resolution
    is `.claude/scripts/helper.sh`, and re-anchoring on that directory makes
    the two agree. Raised by Copilot against the first form, which took the
    event's `cwd` as an anchor whatever it pointed at.

    Walked lexically from the spelling, which is what makes it the right root
    for the case above: `<checkout>/docs/tree` walks to `<checkout>/docs` and
    then to `<checkout>`, where the `.git` is. A worktree's `.git` is a file
    rather than a directory, so this asks whether the entry exists at all.
    """
    current = os.path.abspath(path)
    while True:
        if os.path.exists(os.path.join(current, ".git")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def linked_worktree(path, checkouts):
    """`path`'s checkout root, when that root is a worktree of an anchor's repo.

    **Found by walking into it rather than by reading the code.** `/branch`
    forks a sibling worktree and the session moves into it, so `cwd` is an
    anchor and the ordinary path works. A session standing in the PARENT
    checkout and editing that sibling is a different case: the worktree is a
    checkout, but not one of the three `anchors` knows about, so the target
    resolved outside every anchor and `outside_offence` refused it. That is a
    real edit refused for being in the wrong checkout rather than for landing
    somewhere its path does not spell, which is not this file's subject.

    **Admitting "any checkout" would be the wrong repair**, and it is worth
    saying why: a permission rule's paths are relative to THIS project, so
    `Edit(.claude/scripts/**)` matches nothing in a different repository's
    tree — admitting one would hand the session another repository's machinery
    with no rule able to name it. So the test is narrower: the root must be a
    LINKED WORKTREE of a repository an anchor already stands in.

    Read from the `.git` file rather than by running git, which a hook on every
    write cannot afford: a linked worktree's `.git` is a file reading
    `gitdir: <main>/.git/worktrees/<name>`, and a main checkout's is a
    directory, so the file's existence is itself half the test.
    """
    root = checkout_root(path)
    if root is None:
        return None
    marker = os.path.join(root, ".git")
    if not os.path.isfile(marker):
        return None
    try:
        with open(marker, encoding="utf-8") as handle:
            text = handle.read().strip()
    except OSError:
        return None
    if not text.startswith("gitdir:"):
        return None
    gitdir = os.path.realpath(text.split(":", 1)[1].strip())
    # **The `.git` file is a claim, and git keeps the proof on the other end.**
    # Any directory holding a `.git` file that names `<anchor>/.git/worktrees/x`
    # passed, registered or not, and its ordinary files became writable. A
    # worktree git created has an admin directory whose `gitdir` file points
    # back at this `.git` file, so the backlink is required. Raised by
    # Copilot.
    try:
        with open(os.path.join(gitdir, "gitdir"), encoding="utf-8") as handle:
            backlink = handle.read().strip()
    except OSError:
        return None
    if os.path.normcase(os.path.realpath(backlink)) != os.path.normcase(
            os.path.realpath(marker)):
        return None
    for spelled_root, real_root, traits in checkouts:
        for base in (spelled_root, real_root):
            if under(gitdir, os.path.join(base, ".git"), traits):
                return root
    return None


def protected_in_worktree(path, root):
    """Which protected tree or root file `path` names beneath `root`, or `None`.

    Asked of `guard-git-argv.py`'s `protected_path`, loaded from beside this
    file: that module owns the inventory, and the harness suite asserts it
    covers every tracked root file and machinery tree, so a second list here
    would be the copy that stops covering the newest surface.
    """
    import importlib.util

    try:
        relative = os.path.relpath(path, root)
    except ValueError:
        return None
    spec = importlib.util.spec_from_file_location(
        "guard_git_argv",
        os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "guard-git-argv.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.protected_path(relative)


def anchors(event):
    """The checkouts this guard is standing in, as (spelled, resolved) pairs.

    **Three sources, because no one of them is right in every session.**
    `CLAUDE_PROJECT_DIR` is what the harness sets and what
    `.claude/settings.json` interpolates into this hook's own command line; the
    event's `cwd` is where the session actually is, which differs the moment
    `/branch` moves it into a sibling worktree; and this file's own location is
    the checkout that owns the guard, which is true even if the other two are
    absent or wrong.

    The first and the last are roots by construction — the harness sets one to
    a project root and the other is this file's own tree — so they are taken as
    given. `cwd` is not: it is wherever the session stands, so it is walked up
    to its checkout root and **dropped** when it has none, because a directory
    that belongs to no checkout is not a root and excusing a traversal at it is
    the bypass `checkout_root` documents.

    Each is kept as the pair it is — the spelling and its resolution — because
    the whole judgement below is a comparison between those two, and an anchor
    reached through a link would otherwise make every edit under it look like
    the thing this file refuses.

    **Adding an anchor can only narrow this guard, never widen it**, because
    the caller requires every anchor containing the target to agree. That is
    what makes an environment-supplied `CLAUDE_PROJECT_DIR` safe to trust here:
    a wrong one cannot excuse a traversal that this file's own tree refuses.
    """
    here = os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))
    cwd = event.get("cwd")
    roots = [
        os.environ.get("CLAUDE_PROJECT_DIR"),
        checkout_root(cwd) if isinstance(cwd, str) and cwd else None,
        here,
    ]
    found = []
    for path in roots:
        if not path or not isinstance(path, str):
            continue
        spelled = os.path.abspath(path)
        traits = traits_of(spelled)
        if any(same(spelled, seen, traits) for seen, _, _ in found):
            continue
        found.append((spelled, os.path.realpath(path), traits))
    return found


def offence(event):
    """The reason to refuse this call, or `None` to let it through."""
    tool = event.get("tool_name")
    if tool not in EDITING_TOOLS:
        return None

    # A `tool_input` that is not an object goes to the same refusal as one
    # carrying no path, and the two used to differ: the shape check returned
    # `None` — admit — while the missing key refused. They are the same
    # statement about the same call, which is that this file cannot see where
    # the write lands, and only one of the two answers is the one that fails
    # closed.
    # `name` rather than `key`, which is a function in this module: nothing
    # here calls it after the loop, so the shadow was harmless and the next
    # edit is what it would have cost.
    tool_input = event.get("tool_input")
    spelled = None
    if isinstance(tool_input, dict):
        for name in PATH_KEYS:
            value = tool_input.get(name)
            if isinstance(value, str) and value:
                spelled = value
                break
    if spelled is None:
        return (
            f"guard-edit-target: {tool} carries no file path this guard can "
            "read, so nothing has been established about where it writes. "
            "Refusing rather than waving it through."
        )

    cwd = event.get("cwd")
    if not isinstance(cwd, str) or not cwd:
        cwd = os.getcwd()

    checkouts = anchors(event)
    # **The exemption is per anchor and per target, and it was session-wide.**
    # A checkout on a network share is `\\`-spelled, and the first form read
    # that as licence for any alternate-alphabet target anywhere: once one
    # anchor was UNC, `\\?\UNC\server\share\repo\…` was exempt too, and since
    # it is not lexically under `\\server\share\repo` the anchor loop skipped
    # it and the fall-through admitted it. So the exemption now requires an
    # anchor that is BOTH in that alphabet and containing this target, which
    # is the case it was written for and no other. Raised by Copilot.
    if alternate_alphabet(spelled):
        joined = (spelled if os.path.isabs(spelled)
                  else os.path.join(cwd, spelled))
        placed = os.path.normpath(os.path.abspath(joined))
        if not any(alternate_alphabet(root)
                   and (under(placed, root, traits)
                        or under(placed, real, traits))
                   for root, real, traits in checkouts):
            return (
                f"guard-edit-target: {spelled} is spelled in Windows' other "
                "path grammar — an extended-length or device prefix, or a UNC "
                "share — and no checkout named that way contains it. A "
                "permission rule matches the string it is given, and measured "
                "here a denied directory accepted a write spelled both of "
                "those ways. Name the file the way the rules are written."
            )

    # `realpath` is taken of the ORIGINAL spelling and `normpath` of the joined
    # one, and the order matters: `normpath` collapses `..` lexically, which is
    # the wrong answer for a `..` that follows a link, so the lexical form is
    # used only to locate the target under an anchor. Where the two disagree
    # the call is refused, which is the direction this has to fail in.
    #
    # **A `..` that traverses no link is admitted here, and that is a decision
    # backed by a measurement rather than an oversight.** The argument for
    # refusing it is that `docs/../.claude/hooks/x` carries no `.claude/**`
    # spelling, so a matcher reading the string would not deny it — and the
    # harness does not read the string. Measured in this checkout, with
    # `.claude/sandbox/**` denied: a `Write` to
    # `docs/../.claude/sandbox/probe-tmp.txt` was refused with the harness's own
    # "denied by your permission settings", while `docs/../docs/probe-tmp.txt`
    # was created — so the path is normalised and then matched, and `..` is not
    # what was rejected. Refusing every `..` here would therefore buy nothing
    # against the deny list and would refuse the second of those two, which is
    # innocent traffic. Raised by Copilot; the premise is what failed.
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    lexical = os.path.normpath(os.path.abspath(joined))
    resolved = os.path.realpath(joined)

    # A sibling worktree of a repository an anchor stands in becomes an anchor
    # itself, so the target is JUDGED there rather than refused for being
    # outside. It narrows nothing: the loop below still requires every anchor
    # containing the target to agree, which is the property `anchors` rests its
    # trust in `CLAUDE_PROJECT_DIR` on.
    #
    # **The sibling's `.claude` is refused whether or not the session stands in
    # it, and only its `.claude`.** This check used to be `control_surface`
    # rooted at the whole worktree, which treats `commands`, `scripts` and
    # `plugins` as protected at ANY depth — the reading that function exists
    # for under `~/.claude` — and so refused `src/app/core/commands/`, a real
    # application directory. The rest of the inventory, for a sibling the
    # session is not in, is `protected_in_worktree` below. Raised by Copilot.
    sibling = linked_worktree(lexical, checkouts)
    if sibling is not None:
        try:
            relative = os.path.relpath(lexical, sibling)
        except ValueError:
            relative = ""
        first = relative.replace("\\", "/").split("/")[0]
        if first.rstrip(". ").lower() == ".claude":
            return (
                f"guard-edit-target: {spelled} targets .claude in a linked "
                "worktree, where this session's permission rules do not apply."
            )
    if sibling is not None and not any(
            same(sibling, root, traits) for root, _, traits in checkouts):
        # **The control surface was not the whole inventory.** A sibling the
        # session is not standing in is reached by no permission rule at all,
        # so `../sibling/package.json`, `../sibling/.github/workflows/ci.yml`
        # and `../sibling/.git/config` — each denied to the editing commands
        # in this checkout — agreed with themselves under the new anchor and
        # were admitted. The inventory is the redirection guard's, borrowed
        # rather than copied, because that list is the one the suite holds to
        # `git ls-files`. A sibling that IS an anchor — `/branch` moved the
        # session into it — is judged as the project, where the rules apply.
        # Raised by Copilot.
        machinery = protected_in_worktree(lexical, sibling)
        if machinery is not None:
            return (
                f"guard-edit-target: {spelled} targets {machinery} in a "
                "linked worktree this session is not standing in, where none "
                "of its permission rules apply. Move into the worktree to "
                "edit its machinery or toolchain (docs/harness-boundaries.md)."
            )
        checkouts = checkouts + [
            (sibling, os.path.realpath(sibling), traits_of(sibling))]

    # **Every anchor containing the target must agree, and the first form said
    # ANY.** One agreeing anchor was enough to admit the write, so a second
    # anchor could excuse what the first refused — and that is not hypothetical
    # arithmetic: with `cwd` taken as an anchor whatever it pointed at, a
    # session standing in a linked directory admitted the exact write this file
    # exists to refuse. Requiring agreement is what makes an extra anchor
    # incapable of widening the guard, which is the property `anchors` rests
    # its trust in `CLAUDE_PROJECT_DIR` on. Raised by Copilot.
    judged = False
    for spelled_root, real_root, traits in checkouts:
        if under(lexical, spelled_root, traits):
            base = spelled_root
        elif under(lexical, real_root, traits):
            base = real_root
        else:
            continue
        judged = True

        expected = os.path.normpath(
            os.path.join(real_root, os.path.relpath(lexical, base)))

        # **The traits are measured at the root and a child can disagree**,
        # which makes an equivalence applied there a claim about a directory
        # nobody asked. Windows sets case sensitivity per directory
        # (`fsutil file setCaseSensitiveInfo`), and a mount below the root can
        # differ outright — so `docs/Sub/x.md`, a junction beside a real
        # `docs/sub/`, is two distinct files that the anchor's folding calls
        # one. Measured in a case-sensitive directory here: admitted before
        # this check, refused after. Raised by Copilot.
        #
        # Where the two agree only BECAUSE of an equivalence, the filesystem
        # is asked directly. `samefile` is safe in this position and not in the
        # anchor test: it compares two concrete paths rather than deciding what
        # counts as a root, which is the distinction that makes identity the
        # wrong tool one paragraph up and the right one here. When either path
        # does not exist yet — the ordinary case for `Write` — there is nothing
        # to compare and the folded verdict stands, which is the residual.
        if (same(resolved, expected, traits)
                and os.path.normpath(resolved) != os.path.normpath(expected)):
            try:
                if not os.path.samefile(resolved, expected):
                    return (
                        f"guard-edit-target: {spelled} and the file it names "
                        f"differ only by a spelling this checkout's root folds "
                        f"— but {resolved} and {expected} are two files here. "
                        "A directory may be case- or normalisation-sensitive "
                        "where its root is not (#181, "
                        "docs/harness-boundaries.md)."
                    )
            except OSError:
                pass

        if not same(resolved, expected, traits):
            escaped = not under(resolved, real_root, traits)
            where = "outside the checkout" if escaped else "elsewhere in it"
            return (
                f"guard-edit-target: {spelled} resolves {where} — to "
                f"{resolved}. A permission rule matches the path as written, "
                "so an edit through a link lands where no deny has judged it. "
                "Write the file at its real path, or say why the link is "
                "there (#181, docs/harness-boundaries.md)."
            )

    # **A spelling no anchor recognises, naming a file inside one, is the
    # general form of three separate findings and it is refused here.** The
    # loop above judges a target it can place; everything else fell through to
    # the residual, and the residual is meant for a file that is genuinely
    # outside every checkout — not for one inside a checkout under a name the
    # anchors do not match. That difference is measurable: on a Windows runner
    # `GetShortPathNameW` shortens the whole prefix, so
    # `C:\Users\RUNNER~1\...\GUARD-~1\DOCUME~1\a.md` matched no anchor while
    # resolving squarely inside one, and the case written to pin the 8.3 alias
    # went red on CI having passed locally, where only the leaf was aliased.
    #
    # The same shape produced the case-folding finding and the Unicode one, and
    # both were closed by teaching the comparison a new equivalence. This
    # closes the class instead: whatever the spelling, if it RESOLVES into a
    # checkout that did not recognise it, the matcher judged a string that is
    # not this file and the write is refused. The residual is untouched — a
    # target resolving outside every anchor still falls through, which is what
    # keeps the session's own memory and scratch writes working.
    if not judged:
        for _, real_root, traits in checkouts:
            if under(resolved, real_root, traits):
                return (
                    f"guard-edit-target: {spelled} is not a spelling any "
                    f"checkout here recognises, yet it resolves to {resolved}, "
                    "inside one. A permission rule matches the string it is "
                    "given, so a name the rules cannot place is a write "
                    "nothing has judged (#181, docs/harness-boundaries.md)."
                )

        # **And the target that is genuinely outside every checkout is now
        # bounded rather than admitted (#21).** This was the residual the
        # module docstring stated, and stating it did not make it narrow: it
        # admitted every absolute path that was not in a checkout, which is a
        # shell profile, an SSH key or a credential as readily as a scratch
        # file. `outside_offence` names the two roots the exception was written
        # for and refuses the rest.
        return outside_offence(spelled, lexical, resolved)

    # Reached when every anchor containing the target agreed. A target outside
    # every one of them went to `outside_offence` above.
    return None


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        # The one deliberate fail-OPEN, and it is the argv guard's argument
        # rather than a second decision: a hook that cannot read its own input
        # has established nothing, and refusing every write on a malformed
        # event would turn a defect in this file into a dead session.
        print("guard-edit-target: unreadable hook event; not judging",
              file=sys.stderr)
        return 0

    if not isinstance(event, dict):
        print("guard-edit-target: hook event is not an object; not judging",
              file=sys.stderr)
        return 0

    reason = offence(event)
    if reason is None:
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
