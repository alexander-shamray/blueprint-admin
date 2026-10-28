#!/usr/bin/env python3
"""The pin gate's judge of the build: what it reads from a replayed binary
log and a restore's assets file, what it refuses in them, each beside a
positive control, and the wiring that makes CI run it."""

from __future__ import annotations

import contextlib
import io
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from judge_build import (JudgeError, config_sources, imports, judge, judge_assets, main, pin_versions,
                         read_assets)
from test_pin_gate import inside as cwd, job, run

PIN = """<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Anthropic" Version="1.0.0" />
  </ItemGroup>
</Project>
"""

FEED = "https://api.nuget.org/v3/index.json"


def line(imported: str, importer: str) -> str:
    return f'Importing project "{imported}" into project "{importer}" at (3,3).'


class TheImportLines(unittest.TestCase):
    def test_each_line_names_the_file_and_its_importer_whatever_its_prefix(self) -> None:
        lines = ["                   " + line("/sdk/Sdk.props", "/repo/A.csproj"),
                 "10:01:27.338     0>" + line("/repo/Directory.Build.props", "/sdk/Sdk.props"),
                 "3>" + line("/sdk/x.targets", "/repo/A.csproj") + "\r\n"]
        self.assertEqual(imports(lines), [("/sdk/Sdk.props", "/repo/A.csproj"),
                                          ("/repo/Directory.Build.props", "/sdk/Sdk.props"),
                                          ("/sdk/x.targets", "/repo/A.csproj")])

    def test_other_lines_name_nothing(self) -> None:
        lines = ['Project "/outside/x.props" was not imported by "/repo/A.csproj" at (1,1), due to false condition',
                 "Property reassignment: $(X)=\"/outside/y.props\""]
        self.assertEqual(imports(lines), [])

    def test_an_import_line_it_cannot_read_fails_rather_than_naming_nothing(self) -> None:
        # A quote in a path would let the first half end early, on a prefix
        # inside the repository.
        for text in ['Importing project "/repo/a" into project "b/../../outside.props" into project "/repo/A.csproj" '
                     "at (1,1).",
                     'Importing project "/outside/x.props" into project "/repo/A.csproj".']:
            with self.subTest(text=text), self.assertRaises(JudgeError):
                imports([text])


class TheConfigAndThePinFile(unittest.TestCase):
    def test_a_clear_drops_every_source_named_before_it(self) -> None:
        config = (f'<configuration><packageSources><add key="old" value="https://old.example/" /><clear />'
                  f'<add key="nuget.org" value="{FEED}/" /></packageSources>'
                  '<packageSourceMapping><packageSource key="nuget.org"><package pattern="*" />'
                  "</packageSource></packageSourceMapping></configuration>")
        self.assertEqual(config_sources(config, "/repo"), {FEED.lower()})

    def test_the_pin_files_literal_versions_are_read_by_type_and_name(self) -> None:
        self.assertEqual(pin_versions(PIN), {("packageversion", "anthropic"): "1.0.0"})

    def test_a_doctype_is_refused_in_either(self) -> None:
        with self.assertRaises(JudgeError):
            pin_versions('<!DOCTYPE Project [<!ENTITY e "x">]><Project />')
        with self.assertRaises(JudgeError):
            config_sources('<!DOCTYPE configuration [<!ENTITY e "x">]><configuration />', "/repo")


class TheAssetsFile(unittest.TestCase):
    def test_one_written_after_its_log_fails_rather_than_standing_for_it(self) -> None:
        # Two builds share one assets file: the second's restore rewrites it,
        # so the first's log, judged after it, would read the second's.
        with tempfile.TemporaryDirectory() as scratch:
            path = os.path.join(scratch, "project.assets.json")
            Path(path).write_text("{}", encoding="utf-8")
            written = os.path.getmtime(path)
            os.utime(path, (written + 10, written + 10))
            with self.assertRaises(JudgeError):
                read_assets(path, written)
            self.assertEqual(read_assets(path, written + 10), {})
            self.assertIsNone(read_assets(os.path.join(scratch, "none.json"), written))


class TheJudgement(unittest.TestCase):
    def setUp(self) -> None:
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.base = os.path.realpath(scratch.name)
        self.root = os.path.join(self.base, "repo")
        self.dotnet = os.path.join(self.base, "dotnet")
        self.packages = os.path.join(self.base, "packages")
        self.config = os.path.join(self.root, "nuget.config")
        self.project = os.path.join(self.root, "src", "A", "A.csproj")
        self.obj = os.path.join(self.root, "src", "A", "obj")
        self.pinned = pin_versions(PIN)

    def assets(self, **restore: object) -> dict:
        return {
            "libraries": {"Anthropic/1.0.0": {"type": "package", "path": "anthropic/1.0.0"},
                          "B/1.0.0": {"type": "project", "path": "../B/B.csproj"}},
            "packageFolders": {self.packages + os.sep: {}},
            "project": {
                "restore": {
                    "projectPath": self.project,
                    "packagesPath": self.packages + os.sep,
                    "centralPackageVersionsManagementEnabled": True,
                    "centralPackageVersionOverrideDisabled": True,
                    "configFilePaths": [self.config, os.path.join(self.base, "user", "NuGet.Config")],
                    "fallbackFolders": [os.path.join(self.dotnet, "sdk", "NuGetFallbackFolder")],
                    "sources": {FEED: {}, os.path.join(self.dotnet, "library-packs"): {}},
                    **restore,
                },
                "frameworks": {"net10.0": {
                    "centralPackageVersions": {"Anthropic": "1.0.0"},
                    "dependencies": {"Anthropic": {"target": "Package", "version": "[1.0.0, )",
                                                   "versionCentrallyManaged": True},
                                     "NETStandard.Library": {"target": "Package", "version": "[2.0.3, )",
                                                             "autoReferenced": True}},
                }},
            },
        }

    def imported(self, *extra: tuple[str, str]) -> list[tuple[str, str]]:
        return [(os.path.join(self.dotnet, "sdk", "Sdk.props"), self.project),
                (os.path.join(self.root, "Directory.Build.props"), self.project),
                (os.path.join(self.obj, "A.csproj.nuget.g.props"), self.project),
                (os.path.join(self.packages, "anthropic", "1.0.0", "build", "Anthropic.props"),
                 os.path.join(self.obj, "A.csproj.nuget.g.props")),
                *extra]

    def problems(self, imported: list[tuple[str, str]] | None = None, assets: dict | None = None,
                 tracked: set[str] = frozenset()) -> list[str]:
        restored = {os.path.join(self.obj, "project.assets.json"): assets or self.assets()}
        return judge(self.imported() if imported is None else imported, restored.get, root=self.root,
                     dotnet=self.dotnet, projects=["src/A/A.csproj"], config=self.config,
                     sources={FEED.lower()}, pinned=self.pinned, tracked=set(tracked))

    def refused(self, needle: str, **kwargs: object) -> None:
        problems = self.problems(**kwargs)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn(needle, problems[0])

    def assets_problems(self, assets: dict) -> list[str]:
        return judge_assets(assets, where="src/A/A.csproj", root=self.root, dotnet=self.dotnet,
                            config=self.config, sources={FEED.lower()}, pinned=self.pinned)

    def test_a_build_that_imports_from_the_repository_the_install_and_its_packages_passes(self) -> None:
        self.assertEqual(self.problems(), [])

    def test_an_import_outside_all_three_fails(self) -> None:
        # #562 and #566: a value built from other properties, under a
        # condition keyed on what the build alone sets, lands here once the
        # build has expanded it.
        self.refused("outside the repository",
                     imported=self.imported((os.path.join(self.root, "..", "elsewhere", "x.targets"), self.project)))

    def test_an_import_from_a_sibling_whose_name_starts_with_the_root_fails(self) -> None:
        self.refused("outside the repository",
                     imported=self.imported((self.root + "-other" + os.sep + "x.props", self.project)))

    def test_an_import_from_a_package_restore_did_not_resolve_fails(self) -> None:
        # The cache holds whatever any build on the machine restored; only a
        # package this restore resolved, at the version it resolved, is the
        # pin file's.
        for package in (("anthropic", "9.9.9"), ("other", "1.0.0")):
            with self.subTest(package=package):
                path = os.path.join(self.packages, *package, "build", "x.props")
                self.refused("outside the repository", imported=self.imported((path, self.project)))

    def test_a_project_the_build_never_evaluated_or_restored_fails(self) -> None:
        # With no restore of its own in the log, its packages are no one's.
        problems = self.problems(imported=self.imported()[3:])
        self.assertIn("never evaluated", problems[0])
        self.refused("no restore of it",
                     imported=[pair for pair in self.imported() if not pair[0].endswith("nuget.g.props")][:2])

    def test_restore_output_with_no_assets_file_or_a_tracked_one_fails(self) -> None:
        problems = judge(self.imported(), lambda _: None, root=self.root, dotnet=self.dotnet,
                         projects=["src/A/A.csproj"], config=self.config, sources={FEED.lower()},
                         pinned=self.pinned, tracked=set())
        self.assertTrue(any("no project.assets.json sits beside it" in p for p in problems), problems)
        tracked = {os.path.normcase(os.path.join(self.obj, "project.assets.json"))}
        problems = self.problems(tracked=tracked)
        self.assertTrue(any("is tracked" in p for p in problems), problems)

    def test_an_empty_log_fails_rather_than_refusing(self) -> None:
        # A refusal is what the commits job forgives when the fork point
        # carries it too; an empty log there would forgive every commit's.
        with self.assertRaises(JudgeError):
            self.problems(imported=[])

    def test_central_management_off_or_overridable_fails(self) -> None:
        for name in ("centralPackageVersionsManagementEnabled", "centralPackageVersionOverrideDisabled"):
            with self.subTest(name=name):
                self.refused(name, assets=self.assets(**{name: False}))

    def test_a_nuget_config_other_than_the_roots_fails(self) -> None:
        nested = os.path.join(self.root, "src", "nuget.config")
        self.refused("nearest NuGet config", assets=self.assets(configFilePaths=[nested, self.config]))
        self.refused("nearest NuGet config", assets=self.assets(configFilePaths=[]))
        self.refused("inside the repository", assets=self.assets(configFilePaths=[self.config, nested]))

    def test_a_source_the_root_config_does_not_name_fails(self) -> None:
        for source in ("https://feed.example/v3/index.json", os.path.join(self.root, "feed"),
                       os.path.join(self.base, "feed")):
            with self.subTest(source=source):
                self.refused("does not name", assets=self.assets(sources={FEED + "/": {}, source: {}}))

    def test_a_package_folder_inside_the_repository_fails(self) -> None:
        inner = os.path.join(self.root, "packages")
        self.refused("inside the repository", assets=self.assets(packagesPath=inner))
        self.refused("inside the repository", assets=self.assets(fallbackFolders=[inner]))
        assets = self.assets()
        assets["packageFolders"][inner] = {}
        self.refused("inside the repository", assets=assets)

    def test_central_versions_other_than_the_pin_files_fail(self) -> None:
        for central in ({"Anthropic": "9.9.9"}, {}, {"Anthropic": "1.0.0", "Other": "1.0.0"}):
            with self.subTest(central=central):
                assets = self.assets()
                assets["project"]["frameworks"]["net10.0"]["centralPackageVersions"] = central
                self.assertEqual(len(self.assets_problems(assets)), 1)

    def test_a_dependency_restored_by_a_version_of_its_own_fails(self) -> None:
        assets = self.assets()
        assets["project"]["frameworks"]["net10.0"]["dependencies"]["Anthropic"]["versionCentrallyManaged"] = False
        problems = self.assets_problems(assets)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("a version of its own", problems[0])

    def test_a_pinned_package_resolved_at_another_version_fails(self) -> None:
        assets = self.assets()
        assets["libraries"] = {"Anthropic/9.9.9": {"type": "package", "path": "anthropic/9.9.9"}}
        problems = self.assets_problems(assets)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("not the pin file's 1.0.0", problems[0])


def _dotnet_runs(where: Path) -> bool:
    if shutil.which("dotnet") is None:
        return False
    return subprocess.run(["dotnet", "--version"], cwd=where, capture_output=True, check=False).returncode == 0


# The SDK's own target framework, so the case restores no package and runs
# offline on whatever SDK the runner has.
PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net$(BundledNETCoreAppTargetFrameworkVersion)</TargetFramework>
{properties}
  </PropertyGroup>
{items}
</Project>
"""


class ALiveBuild(unittest.TestCase):
    """MSBuild itself, building a repository made for the case: what the unit
    cases above assume about the log and the assets file is held to the real
    thing, and so are the contexts #566 names."""

    def tree(self, **projects: tuple[str, str]) -> tuple[Path, Path]:
        scratch = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(scratch.cleanup)
        base = Path(os.path.realpath(scratch.name))
        if not _dotnet_runs(base):
            self.skipTest("no .NET SDK runs here")
        (base / "feed").mkdir()
        root = base / "repo"
        root.mkdir()
        # Each stops MSBuild's walk upward at the repository's root.
        (root / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
        (root / "Directory.Build.targets").write_text("<Project />", encoding="utf-8")
        (root / "Directory.Packages.props").write_text(PIN, encoding="utf-8")
        (root / "nuget.config").write_text(
            f'<configuration><packageSources><clear /><add key="local" value="{base / "feed"}" />'
            "</packageSources></configuration>", encoding="utf-8")
        for name, (properties, items) in projects.items():
            (root / name).mkdir()
            (root / name / f"{name}.csproj").write_text(PROJECT.format(properties=properties, items=items),
                                                        encoding="utf-8")
        with cwd(root):
            run("git", "init", "-q")
            run("git", "add", ".")
        return base, root

    def build(self, base: Path, root: Path, project: str) -> tuple[int, str]:
        binlog = base / "build.binlog"
        done = subprocess.run(["dotnet", "build", project, f"-bl:{binlog}", "-nologo"], cwd=root,
                              capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
                              env={**os.environ, "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        out = io.StringIO()
        with cwd(root), contextlib.redirect_stdout(out):
            status = main([str(binlog)])
        return status, out.getvalue()

    def test_a_build_that_imports_nothing_from_outside_passes(self) -> None:
        base, root = self.tree(A=("", '  <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup>'),
                               B=("", ""))
        status, out = self.build(base, root, "A/A.csproj")
        self.assertEqual(status, 0, out)

    def test_an_import_only_restore_or_restore_output_takes_fails(self) -> None:
        # A's import is keyed on a global property restore alone sets, B's on
        # restore's own output: an evaluation before the build sees neither.
        up = "    <Up>$(MSBuildThisFileDirectory)..\\..</Up>\n"
        base, root = self.tree(
            A=(up + "    <CustomAfterMicrosoftCommonTargets Condition=\"'$(MSBuildIsRestoring)' == 'true'\">"
                    "$(Up)\\restoring.targets</CustomAfterMicrosoftCommonTargets>",
               '  <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup>'),
            B=(up + "    <CustomAfterMicrosoftCommonTargets "
                    "Condition=\"Exists('$(MSBuildThisFileDirectory)obj\\project.assets.json')\">"
                    "$(Up)\\restored.targets</CustomAfterMicrosoftCommonTargets>", ""))
        for name in ("restoring.targets", "restored.targets"):
            (base / name).write_text("<Project />", encoding="utf-8")
        status, out = self.build(base, root, "A/A.csproj")
        self.assertEqual(status, 1, out)
        for name in ("restoring.targets", "restored.targets"):
            errors = [line for line in out.splitlines() if name in line]
            self.assertEqual(len(errors), 1, out)
            self.assertIn("build.binlog: ", errors[0])
            self.assertIn("outside the repository", errors[0])


class TheCiWiring(unittest.TestCase):
    # A gate nothing runs reads exactly like one that passes.

    def test_the_host_job_judges_each_build_on_both_legs_before_the_next(self) -> None:
        # A condition can key an import on the operating system or the
        # configuration, so neither leg's log stands for the other's; and the
        # two builds share one assets file, so each is judged before the next
        # restores over it (R1).
        block = job("host")
        judge = "- run: ${{ runner.os == 'Windows' && 'python' || 'python3' }} .github/pin-gate/judge_build.py "
        order = ["- run: dotnet build -bl:${{ runner.temp }}/debug.binlog",
                 judge + "${{ runner.temp }}/debug.binlog\n",
                 "- run: dotnet build --configuration Release -bl:${{ runner.temp }}/release.binlog",
                 judge + "${{ runner.temp }}/release.binlog\n",
                 "- run: dotnet test"]
        at = 0
        for text in order:
            self.assertIn(text, block[at:], text)
            at = block.index(text, at) + len(text)
        # `- run:` opens each judge step, so no `if:` precedes it; none may follow.
        for step in (order[1], order[3]):
            self.assertNotIn("if:", block[block.index(step) + len(step):].split("\n", 1)[0])

    def test_the_commits_job_judges_each_commits_build(self) -> None:
        # The static read before the build, the judge of its log after, each
        # held to the fork point's refusals (#563).
        block = job("commits")
        self.assertIn('cp .github/pin-gate/judge_build.py "$RUNNER_TEMP/judge_build.py"', block)
        self.assertIn('-bl:"$RUNNER_TEMP/build.binlog"', block[block.index("build() {"):])
        fork = block[block.index("git merge-base"):block.index('for sha in "${shas[@]}"')]
        self.assertIn('refusals judge_build.py "$RUNNER_TEMP/build.binlog"', fork)
        loop = block[block.index('for sha in "${shas[@]}"'):]
        order = ["git clean", "found=$(refusals pin_gate.py)", 'held "$found"', "\n            build\n",
                 'found=$(refusals judge_build.py "$RUNNER_TEMP/build.binlog")', 'held "$found"']
        at = 0
        for text in order:
            self.assertIn(text, loop[at:], text)
            at = loop.index(text, at) + len(text)
        held = block[block.index("held() {"):]
        self.assertIn('if [ -n "$added" ]; then printf \'%s\\n\' "$added"; exit 1; fi', held)


if __name__ == "__main__":
    unittest.main()
