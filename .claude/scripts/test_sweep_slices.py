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
    "src/Admin.Web/src/app/b.ts": "x\n", "src/Admin.Web/src/app/b.spec.ts": "x\n",
    "src/Admin.Web/e2e/c.spec.ts": "x\n", "src/Admin.Web/package.json": "x\n",
    "tests/Admin.Host.Tests/d.cs": "x\n", "tools/e.py": "x\n", ".github/f.yml": "x\n",
    ".claude/g.md": "x\n", "README.md": "x\n", "docs/i.md": "```sh\nx\n```\n",
}


class ASmallRepository(unittest.TestCase):
    """A small repository of its own, so a commit and a ref never touch this one."""

    def setUp(self):
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
                "host": ["src/Admin.Host/a.cs"],
                "spa": ["src/Admin.Web/src/app/b.ts"],
                "suites": [
                    "src/Admin.Web/e2e/c.spec.ts", "src/Admin.Web/src/app/b.spec.ts",
                    "tests/Admin.Host.Tests/d.cs",
                ],
                "tooling": [".claude/g.md", ".github/f.yml", "tools/e.py"],
                "config": [
                    "README.md", "src/Admin.Host/Admin.Host.csproj", "src/Admin.Web/package.json",
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
            '{\n  "packages": {\n    "node_modules/x": {\n'
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
        sound = entry % ("https://registry.npmjs.org/x/-/x-1.0.0.tgz", integrity.rstrip("\n") + ",\n")
        lock = '{\n  "packages": {\n    "": {\n      "name": "a"\n    },\n%s\n  }\n}\n'
        self.commit("src/Admin.Web/package-lock.json", lock % sound)
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, parse(result.stdout)[0]["counts"]["generated"])
        off = entry % ("https://evil.example/x.tgz", integrity.rstrip("\n") + ",\n")
        self.commit("src/Admin.Web/package-lock.json", lock % off)
        result = Sweep(self, self.repo).slices("security", "full")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("resolved off the registry: node_modules/x", result.stderr)
        bare = entry % ("https://registry.npmjs.org/x/-/x-1.0.0.tgz", "")
        self.commit("src/Admin.Web/package-lock.json", lock % bare.replace('tgz",\n    }', 'tgz"\n    }'))
        result = Sweep(self, self.repo).slices("bug", "full")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("no integrity: node_modules/x", result.stderr)

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
    """Each command's row table names exactly the rows `row_of` can print."""

    def test_each_table_matches_row_of(self):
        source = SLICES.read_text(encoding="utf-8")
        body = source[source.index("row_of() {"):source.index("\n}\n", source.index("row_of() {"))]
        arms = re.findall(r"^\s*(.+?)\) echo ([a-z-]+) ;;$", body, re.MULTILINE)
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
