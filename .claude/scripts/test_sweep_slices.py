"""sweep-slices.sh and sweep-mark.sh: the partition, the budget and the ref.

The partition runs against this repository's own tree, so a new top-level
directory no row owns fails here before a sweep passes over it.

**Two Windows pitfalls the backend met first, both designed around.** The helper
prints a POSIX `/tmp/...` path that Windows Python reads as `C:\\tmp\\...`, so
every list is read through bash and never through `pathlib`. And a suite that
checked this whole repository out per test was slow, so everything but the real
tree runs over a small synthetic repository.
"""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
SLICES = SCRIPTS / "sweep-slices.sh"
MARK = SCRIPTS / "sweep-mark.sh"
DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"
REPO = str(SCRIPTS.parent.parent)
BUDGET = 240000
GIT_ID = "-c user.name=t -c user.email=t@example.invalid"

BASH = shutil.which("bash")
GIT = shutil.which("git")


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass, the fail-open
    # test_harness_helpers.py refuses for the same two tools.
    missing = [n for n, p in (("bash", BASH), ("git", GIT)) if p is None]
    if missing:
        raise RuntimeError(f"{', '.join(missing)} required and not on PATH")


def run_bash(script, **env_extra):
    """A bash fragment with every value in the environment, never in argv, which
    MSYS re-parses on the way into bash.exe."""
    env = dict(os.environ)
    env.update(env_extra)
    return subprocess.run([BASH, "-c", script], capture_output=True, text=True, env=env)


def git(repo, args):
    result = run_bash(f'cd "$R" && git {GIT_ID} {args}', R=repo)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()


class Sweep:
    """A detached sweep worktree of `repo`, dropped with its slices afterwards."""

    def __init__(self, case, repo):
        self.repo = repo
        sha = git(repo, "rev-parse HEAD")
        made = run_bash(
            'cd "$R" && bash "$DETACH" "$SHA" 2>/dev/null', R=repo, DETACH=str(DETACH), SHA=sha
        )
        case.assertEqual(0, made.returncode, made.stderr)
        self.path = made.stdout.strip()
        case.addCleanup(
            lambda: run_bash(
                'cd "$R" && bash "$DROP" "$W" >/dev/null 2>&1; rm -rf "$W.slices"',
                R=repo, DROP=str(DROP), W=self.path,
            )
        )

    def slices(self, kind, *extra):
        return run_bash(
            'cd "$R" && bash "$SLICES" "$KIND" "$W" $EXTRA',
            R=self.repo, SLICES=str(SLICES), KIND=kind, W=self.path, EXTRA=" ".join(extra),
        )


def parse(stdout):
    head, slices = {}, []
    for line in stdout.splitlines():
        word, _, rest = line.partition(" ")
        if word == "slice":
            n, row, files, size, listing = rest.split(" ")
            slices.append((row, int(files), int(size), listing))
        elif word == "tracked":
            words = line.split(" ")
            head["counts"] = {k: int(v) for k, v in zip(words[::2], words[1::2])}
        else:
            head[word] = rest
    return head, slices


def listed(slices):
    """The listed paths, read by bash: the helper prints its own spelling, which
    under MSYS is a POSIX path Python resolves to another directory."""
    paths = []
    for *_, listing in slices:
        read = run_bash('cat -- "$P"', P=listing)
        assert read.returncode == 0, read.stderr
        paths += read.stdout.splitlines()
    return paths


class TheRealTree(unittest.TestCase):
    """Both partitions hold over this repository, within the budget."""

    def test_every_tracked_path_is_listed_or_counted_and_every_slice_fits(self):
        # `full`, so a ref a real sweep left in this checkout does not narrow it.
        sweep = Sweep(self, REPO)
        tracked = len(git(REPO, "ls-tree -r --name-only --full-tree HEAD").splitlines())
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                run_bash('rm -rf "$W.slices"', W=sweep.path)
                result = sweep.slices(kind, "full")
                self.assertEqual(0, result.returncode, result.stderr)
                head, slices = parse(result.stdout)
                self.assertEqual("full", head["mode"])
                counts = head["counts"]
                self.assertEqual(tracked, counts["tracked"])
                skipped = sum(v for k, v in counts.items() if k not in ("tracked", "listed"))
                self.assertEqual(counts["tracked"], counts["listed"] + skipped)
                for row, files, size, _ in slices:
                    self.assertTrue(size <= BUDGET or files == 1, (row, files, size))
                paths = listed(slices)
                self.assertEqual(counts["listed"], len(paths))
                self.assertEqual(len(paths), len(set(paths)), "a path in two slices")
                for top in ("src/", "tests/", "tools/", ".claude/", ".github/", "docs/"):
                    self.assertTrue(any(p.startswith(top) for p in paths), top)
                self.assertTrue(any("/" not in p for p in paths), "the root files")
                self.assertFalse(any(p.startswith("docs/superpowers/plans/") for p in paths))
                self.assertFalse(any(p.endswith("package-lock.json") for p in paths))


# One file in every row both sweeps own, so a small repository partitions the
# way this one does; a clone of this one checks out every file per test.
SMALL_TREE = {
    "src/Admin.Host/a.cs": "x\n", "src/Admin.Host/Admin.Host.csproj": "x\n",
    "src/Admin.Mcp/m.cs": "x\n", "src/Admin.Mcp/Admin.Mcp.csproj": "x\n",
    "src/Admin.Web/src/app/b.ts": "x\n", "src/Admin.Web/src/app/b.spec.ts": "x\n",
    "src/Admin.Web/e2e/c.spec.ts": "x\n", "src/Admin.Web/package.json": "x\n",
    "tests/Admin.Host.Tests/d.cs": "x\n", "tools/e.py": "x\n", ".github/f.yml": "x\n",
    ".claude/g.md": "x\n", "README.md": "x\n", "docs/i.md": "```sh\nx\n```\n",
}


class ASmallRepository(unittest.TestCase):
    """A small repository of its own, so a commit and a ref never touch this one.

    And a temp root of its own: the detach, drop and slices helpers make and
    accept sweep worktrees only directly under `${TMPDIR:-/tmp}`, so with
    `TMPDIR` pointed at a fresh directory this class shares no `secsweep-*`
    namespace with `TheRealTree` or a real sweep, and the shard runner need
    not keep it on the same worker as them (#322). The cleanups run last in,
    first out, so the sweep is dropped while `TMPDIR` still names its root.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="sweeproot-")
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        # A forward-slashed path, which MSYS bash takes as it stands; the
        # helpers resolve it with `cd` and `pwd -P` themselves.
        private = mock.patch.dict(os.environ,
                                  {"TMPDIR": Path(self.root).as_posix()})
        private.start()
        self.addCleanup(private.stop)
        self.repo = tempfile.mkdtemp(prefix="sweepslices-")
        self.addCleanup(shutil.rmtree, self.repo, ignore_errors=True)
        made = run_bash('cd "$R" && git init -q --initial-branch=main .', R=self.repo)
        self.assertEqual(0, made.returncode, made.stderr)
        for relative, text in SMALL_TREE.items():
            target = Path(self.repo, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text)
        git(self.repo, "add -A")
        git(self.repo, 'commit -q -m "the small tree"')

    def commit(self, relative, text):
        target = Path(self.repo, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        git(self.repo, f'add -- "{relative}"')
        git(self.repo, f'commit -q -m "add {relative}"')
        return git(self.repo, "rev-parse HEAD")

    def mark(self, kind, sha):
        return run_bash(
            'cd "$R" && bash "$MARK" "$KIND" "$SHA"', R=self.repo, MARK=str(MARK), KIND=kind, SHA=sha
        )

    def test_its_sweeps_land_in_its_own_temp_root(self):
        # What lets the runner deal this class anywhere: if a helper stopped
        # honouring TMPDIR under MSYS, its sweeps would land in the shared
        # root beside `TheRealTree`'s again, and nothing else would notice.
        sweep = Sweep(self, self.repo)
        result = sweep.slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        where = run_bash('cd "$TMPDIR" && pwd -P && cd "$(dirname "$W")" && pwd -P',
                         W=sweep.path)
        self.assertEqual(0, where.returncode, where.stderr)
        root, parent = where.stdout.split()
        self.assertEqual(root, parent)
        listing = parse(result.stdout)[1][0][3]
        self.assertTrue(listing.startswith(f"{sweep.path}.slices/"), listing)

    def test_each_row_owns_the_paths_its_command_says(self):
        sweep = Sweep(self, self.repo)
        result = sweep.slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        by_row = {}
        for row, _, _, listing in slices:
            by_row.setdefault(row, []).extend(listed([(row, 0, 0, listing)]))
        self.assertEqual(
            {
                "host": ["src/Admin.Host/a.cs", "src/Admin.Mcp/m.cs"],
                "spa": ["src/Admin.Web/src/app/b.ts"],
                "suites": [
                    "src/Admin.Web/e2e/c.spec.ts", "src/Admin.Web/src/app/b.spec.ts",
                    "tests/Admin.Host.Tests/d.cs",
                ],
                "tooling": [".claude/g.md", ".github/f.yml", "tools/e.py"],
                "config": [
                    "README.md", "src/Admin.Host/Admin.Host.csproj",
                    "src/Admin.Mcp/Admin.Mcp.csproj", "src/Admin.Web/package.json",
                ],
                "samples": ["docs/i.md"],
            },
            {row: sorted(paths) for row, paths in by_row.items()},
        )

    def test_a_top_level_directory_no_row_owns_refuses_the_run(self):
        self.commit("newtree/x.txt", "x\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(3, result.returncode)
        self.assertIn("no row owns: newtree/x.txt", result.stderr)

    def test_a_new_source_project_refuses_the_run_rather_than_joining_a_row(self):
        self.commit("src/Admin.Other/x.cs", "x\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(3, result.returncode)
        self.assertIn("no row owns: src/Admin.Other/x.cs", result.stderr)

    def test_a_marked_ancestor_narrows_round_one_to_the_change(self):
        base = git(self.repo, "rev-parse HEAD")
        self.assertEqual(0, self.mark("bug", base).returncode)
        self.commit("tools/added.py", "print(1)\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(f"since {base}", head["mode"])
        self.assertEqual(["tools/added.py"], listed(slices))

    def test_a_sample_counts_its_fences_and_a_doc_without_one_is_counted_not_listed(self):
        self.assertEqual(0, self.mark("bug", git(self.repo, "rev-parse HEAD")).returncode)
        body = "```cs\nvar x = 1;\n```\n"
        nested = "````md\n```\ninner\n```\n````\nprose after\n"
        self.commit("docs/fenced.md", "# t\n" + body)
        self.commit("docs/nested.md", nested)
        self.commit("docs/plain.md", "no code here\n")
        self.commit("docs/superpowers/plans/p.md", "```sh\nx\n```\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(["docs/fenced.md", "docs/nested.md"], sorted(listed(slices)))
        self.assertEqual(1, head["counts"]["fenceless"])
        self.assertEqual(1, head["counts"]["record"])
        # The fenced line alone; the nested fence's body is its three inner lines.
        self.assertEqual(len("var x = 1;\n") + len("```\ninner\n```\n"), sum(s[2] for s in slices))

    def test_the_lockfile_is_owned_and_counted_not_read(self):
        self.commit(
            "src/Admin.Web/package-lock.json",
            '{\n  "lockfileVersion": 3,\n  "packages": {\n    "node_modules/x": {\n'
            '      "resolved": "https://registry.npmjs.org/x/-/x-1.0.0.tgz",\n'
            '      "integrity": "sha512-AAAA"\n    }\n  }\n}\n',
        )
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(1, head["counts"]["generated"])
        self.assertNotIn("src/Admin.Web/package-lock.json", listed(slices))

    def test_a_binary_asset_is_counted_and_a_binary_code_file_refuses_the_run(self):
        Path(self.repo, "src/Admin.Web/public").mkdir(parents=True)
        Path(self.repo, "src/Admin.Web/public/logo.PNG").write_bytes(b"a\0b")
        git(self.repo, "add -A")
        git(self.repo, 'commit -q -m "an image"')
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, parse(result.stdout)[0]["counts"]["binary"])
        Path(self.repo, "tools").mkdir(exist_ok=True)
        Path(self.repo, "tools/blob.py").write_bytes(b"print(1)\0\n")
        git(self.repo, "add -A")
        git(self.repo, 'commit -q -m "a code file with a NUL"')
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("tools/blob.py", result.stderr)

    def test_a_lockfile_resolving_off_the_registry_or_missing_an_integrity_refuses_the_run(self):
        entry = (
            '    "node_modules/x": {\n      "version": "1.0.0",\n'
            '      "resolved": "%s",\n%s    }'
        )
        integrity = '      "integrity": "sha512-AAAA"\n'
        sound = entry % ("https://registry.npmjs.org/x/-/x-1.0.0.tgz", integrity)
        link = '    "node_modules/w": {\n      "resolved": "../w",\n      "link": true\n    },\n'
        lock = '{\n  "lockfileVersion": 3,\n  "packages": {\n    "": {\n      "name": "a"\n    },\n' + link + '%s\n  }\n}\n'
        self.commit("src/Admin.Web/package-lock.json", lock % sound)
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, parse(result.stdout)[0]["counts"]["generated"])
        off = entry % ("https://evil.example/x.tgz", integrity)
        self.commit("src/Admin.Web/package-lock.json", lock % off)
        result = Sweep(self, self.repo).slices("security", "full")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("resolved off the registry: node_modules/x", result.stderr)
        bare = entry % ("https://registry.npmjs.org/x/-/x-1.0.0.tgz", "")
        self.commit("src/Admin.Web/package-lock.json", lock % bare.replace('tgz",\n    }', 'tgz"\n    }'))
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("no integrity: node_modules/x", result.stderr)

    def test_a_lockfile_is_read_as_json_so_its_whitespace_cannot_hide_an_entry(self):
        # #283: `npm ci` parses the file and ignores whitespace, so the check
        # that matched npm's own indentation was passed by reformatting one entry.
        path = "src/Admin.Web/package-lock.json"
        registry = "https://registry.npmjs.org/x/-/x-1.0.0.tgz"

        def lock(resolved, integrity='"integrity":"sha512-AAAA",'):
            return (
                '{"lockfileVersion":3,"packages":{"":{"name":"a"},"node_modules/ok":{"resolved":"%s","integrity":"sha512-BBBB"},'
                '"node_modules/x":{%s"version":"1.0.0","resolved":"%s"}}}\n'
            ) % (registry, integrity, resolved)

        self.commit(path, lock(registry))
        result = Sweep(self, self.repo).slices("security", "full")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, parse(result.stdout)[0]["counts"]["generated"])

        # A bundled entry is exempt from integrity, as a link is exempt from both.
        self.commit(
            path,
            '{"lockfileVersion":3,"packages":{"node_modules/b":{"resolved":"%s","inBundle":true},'
            '"node_modules/w":{"resolved":"../w","link":true}}}\n' % registry,
        )
        result = Sweep(self, self.repo).slices("security", "full")
        self.assertEqual(0, result.returncode, result.stderr)

        cases = {
            "one-line entry off the registry": (
                lock("https://evil.example/x.tgz"), "resolved off the registry: node_modules/x"),
            "resolved at an odd indent": (
                '{\n "lockfileVersion": 3, "packages": {\n  "node_modules/x": {\n       "resolved": "https://evil.example/x.tgz",\n'
                '  "integrity": "sha512-AAAA" } } }\n',
                "resolved off the registry: node_modules/x"),
            "one-line entry with no integrity": (
                lock(registry, integrity=""), "no integrity: node_modules/x"),
            "an http registry": (
                lock("http://registry.npmjs.org/x/-/x-1.0.0.tgz"), "resolved off the registry: node_modules/x"),
            "not JSON": ("{ not json\n", "the lockfile is not valid JSON"),
            "no packages": ('{"lockfileVersion":3}\n', "no package entries read"),
            # npm ci reads `dependencies` for a version 1 file, which this check does not walk.
            "a version 1 file": (
                '{"lockfileVersion":1,"dependencies":{"x":{"resolved":"https://evil.example/x.tgz"}}}\n',
                "lockfileVersion is not 2 or 3"),
            "no version": ('{"packages":{"node_modules/x":{}}}\n', "lockfileVersion is not 2 or 3"),
            "a non-object entry": (
                '{"lockfileVersion":3,"packages":{"node_modules/x":5}}\n', "malformed entry: node_modules/x"),
            "a non-string resolved": (
                '{"lockfileVersion":3,"packages":{"node_modules/x":{"resolved":5,"integrity":"sha512-A"}}}\n',
                "resolved off the registry: node_modules/x"),
            "a non-string integrity": (
                '{"lockfileVersion":3,"packages":{"node_modules/x":{"resolved":"%s","integrity":5}}}\n' % registry,
                "no integrity: node_modules/x"),
        }
        for label, (content, expected) in cases.items():
            with self.subTest(label):
                self.commit(path, content)
                result = Sweep(self, self.repo).slices("security", "full")
                self.assertEqual(3, result.returncode, result.stderr)
                self.assertIn(expected, result.stderr)

    def test_full_and_a_non_ancestor_ref_both_read_everything(self):
        self.commit("tools/added.py", "print(1)\n")
        tip = git(self.repo, "rev-parse HEAD")
        sweep = Sweep(self, self.repo)
        self.assertEqual(0, self.mark("bug", tip).returncode)
        result = sweep.slices("bug", "full")
        self.assertEqual("full", parse(result.stdout)[0]["mode"])
        # A side commit is no ancestor of the swept one.
        git(self.repo, "checkout -q --detach HEAD~1")
        side = self.commit("tools/side.py", "print(2)\n")
        self.assertEqual(0, self.mark("bug", side).returncode)
        run_bash('rm -rf "$W.slices"', W=sweep.path)
        result = sweep.slices("bug")
        self.assertEqual("full", parse(result.stdout)[0]["mode"], result.stderr)

    def test_mark_takes_a_closed_kind_and_a_full_existing_commit(self):
        sha = git(self.repo, "rev-parse HEAD")
        for kind, commit, code in (
            ("other", sha, 2), ("bug", sha[:12], 2), ("bug", "0" * 40, 3), ("bug", "HEAD", 2)
        ):
            with self.subTest(kind=kind, commit=commit):
                self.assertEqual(code, self.mark(kind, commit).returncode)
        self.assertEqual(0, self.mark("security", sha).returncode)
        self.assertEqual(sha, git(self.repo, "rev-parse refs/sweeps/security"))

    def test_the_slices_go_with_the_worktree(self):
        sweep = Sweep(self, self.repo)
        self.assertEqual(0, sweep.slices("security").returncode)
        dropped = run_bash(
            'cd "$R" && bash "$DROP" "$W" && [ ! -e "$W.slices" ] && echo gone',
            R=self.repo, DROP=str(DROP), W=sweep.path,
        )
        self.assertEqual("gone", dropped.stdout.strip(), dropped.stderr)


class TheTablesNameTheHelpersRows(unittest.TestCase):
    """Each command's row table names exactly the rows `row_of` can return."""

    def test_each_table_matches_row_of(self):
        source = SLICES.read_text(encoding="utf-8")
        body = source[source.index("row_of() {"):source.index("\n}\n", source.index("row_of() {"))]
        arms = re.findall(r"^\s*(.+?)\) REPLY=([a-z-]+) ;;$", body, re.MULTILINE)
        self.assertGreater(len(arms), 5, "the arm pattern stopped matching row_of")
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                rows = {
                    row for pattern, row in arms
                    if pattern == "*" or any(p.split(":")[0] in (kind, "*") for p in pattern.split("|"))
                }
                text = (SCRIPTS.parent / "commands" / f"{kind}-sweep.md").read_text(encoding="utf-8")
                table = set(re.findall(r"^   \| `([a-z-]+)` \|", text, re.MULTILINE))
                self.assertEqual(rows, table)


class Arguments(unittest.TestCase):
    def test_a_path_not_shaped_like_a_sweep_worktree_is_refused(self):
        result = run_bash('bash "$SLICES" bug "$P"', SLICES=str(SLICES), P=REPO)
        self.assertEqual(2, result.returncode)

    def test_kind_and_mode_are_closed_sets(self):
        for args in ("other /tmp", "bug /tmp since", "bug"):
            with self.subTest(args=args):
                result = run_bash(f'bash "$SLICES" {args}', SLICES=str(SLICES))
                self.assertEqual(2, result.returncode)


if __name__ == "__main__":
    unittest.main()
