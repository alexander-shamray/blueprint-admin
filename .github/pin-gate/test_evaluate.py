#!/usr/bin/env python3
"""The pin gate's evaluation: what it reads from MSBuild, what it refuses in
it, each beside a positive control, and the wiring that makes CI run it."""

from __future__ import annotations

import contextlib
import io
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from evaluate import EvaluationError, imports, judge, main, pin_versions
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


def banner(path: str) -> str:
    rule = "=" * 40
    return f'  <!--\n{rule}\n  <Import Project="x.props">\n\n{path}\n{rule}\n-->\n'


def preprocessed(*paths: str, body: str = "") -> str:
    return "<Project>\n" + "".join(banner(p) for p in paths) + body + "</Project>\n"


class TheImportBanners(unittest.TestCase):
    def test_each_banner_names_the_file_it_imported(self) -> None:
        text = preprocessed("/sdk/Sdk.props", "/repo/Directory.Build.props")
        self.assertEqual(imports(text), ["/sdk/Sdk.props", "/repo/Directory.Build.props"])

    def test_the_closing_banner_and_other_comments_name_nothing(self) -> None:
        rule = "=" * 40
        text = preprocessed("/sdk/Sdk.props", body=f"  <!--\n{rule}\n  </Import>\n\n/sdk/Sdk.props\n{rule}\n-->\n"
                                                    "  <!-- /elsewhere/x.props -->\n")
        self.assertEqual(imports(text), ["/sdk/Sdk.props"])

    def test_a_cdata_section_cannot_open_a_comment_that_hides_a_banner(self) -> None:
        # A text search for `<!--` would start a comment inside the CDATA and
        # run it to the next banner's `-->`, so the import after it vanished.
        text = ("<Project>\n  <PropertyGroup><P><![CDATA[<!--]]></P></PropertyGroup>\n"
                + banner("/outside/x.props") + "</Project>\n")
        self.assertEqual(imports(text), ["/outside/x.props"])

    def test_output_that_does_not_parse_fails_rather_than_naming_nothing(self) -> None:
        with self.assertRaises(EvaluationError):
            imports("<Project>" + banner("/outside/x.props"))

    def test_a_doctype_is_refused_unexpanded(self) -> None:
        with self.assertRaises(EvaluationError):
            imports('<!DOCTYPE Project [<!ENTITY e "x">]><Project>&e;</Project>')


class ThePinFile(unittest.TestCase):
    def test_its_literal_versions_are_read_by_type_and_name(self) -> None:
        self.assertEqual(pin_versions(PIN), {("packageversion", "anthropic"): "1.0.0"})

    def test_a_doctype_is_refused(self) -> None:
        with self.assertRaises(EvaluationError):
            pin_versions('<!DOCTYPE Project [<!ENTITY e "x">]><Project />')


class TheJudgement(unittest.TestCase):
    def setUp(self) -> None:
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.base = os.path.realpath(scratch.name)
        self.root = os.path.join(self.base, "repo")
        self.dotnet = os.path.join(self.base, "dotnet")
        self.pinned = pin_versions(PIN)

    def evaluated(self, **properties: str) -> dict:
        pin = os.path.join(self.root, "Directory.Packages.props")
        return {
            "Properties": {
                "ManagePackageVersionsCentrally": "true",
                "CentralPackageVersionOverrideEnabled": "false",
                "DirectoryPackagesPropsPath": pin,
                "RestoreAdditionalProjectFallbackFolders": ";" + os.path.join(self.dotnet, "sdk", "x", "..",
                                                                                "NuGetFallbackFolder"),
                **properties,
            },
            "Items": {
                "PackageVersion": [{"Identity": "Anthropic", "Version": "1.0.0", "DefiningProjectFullPath": pin}],
                "PackageReference": [{"Identity": "Anthropic"}],
            },
        }

    def problems(self, evaluated: dict | None = None, imported: list[str] | None = None) -> list[str]:
        return judge("src/A/A.csproj", "Release", evaluated or self.evaluated(),
                     imported if imported is not None else [
                         os.path.join(self.dotnet, "sdk", "Sdk.props"),
                         os.path.join(self.root, "Directory.Build.props")],
                     root=self.root, dotnet=self.dotnet, pinned=self.pinned)

    def refused(self, needle: str, evaluated: dict | None = None, imported: list[str] | None = None) -> None:
        problems = self.problems(evaluated, imported)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("src/A/A.csproj (Release)", problems[0])
        self.assertIn(needle, problems[0])

    def test_a_project_that_imports_from_the_repository_and_the_install_passes(self) -> None:
        self.assertEqual(self.problems(), [])

    def test_an_import_outside_both_fails(self) -> None:
        # #562: a value built from other properties, which no static read
        # can expand, lands here once MSBuild has expanded it.
        self.refused("outside the repository",
                     imported=[os.path.join(self.root, "..", "elsewhere", "x.targets")])

    def test_an_import_from_a_sibling_whose_name_starts_with_the_root_fails(self) -> None:
        self.refused("outside the repository", imported=[self.root + "-other" + os.sep + "x.props"])

    def test_a_feed_or_a_nuget_setting_fails(self) -> None:
        for name in ("RestoreSources", "RestoreConfigFile", "RestoreRootConfigDirectory"):
            with self.subTest(name=name):
                self.refused(name, self.evaluated(**{name: "https://feed.example/v3/index.json"}))

    def test_a_package_folder_or_local_source_outside_the_install_fails(self) -> None:
        # The install's own (a Windows runner's SDK adds its library-packs
        # as a source where the folder exists) passes; the repository does
        # not, since restore takes a package already extracted there as it is.
        for name in ("RestorePackagesPath", "RestoreFallbackFolders", "RestoreAdditionalProjectFallbackFolders",
                     "RestoreAdditionalProjectSources"):
            with self.subTest(name=name):
                self.assertEqual(self.problems(self.evaluated(**{name: ";" + os.path.join(self.dotnet,
                                                                                           "library-packs")})), [])
                self.refused(name, self.evaluated(**{name: os.path.join(self.base, "packages")}))
                self.refused(name, self.evaluated(**{name: "../../../packages"}))
                self.refused(name, self.evaluated(**{name: "../../artifacts/packages"}))
                self.refused(name, self.evaluated(**{name: "https://feed.example/v3/index.json"}))

    def test_central_management_turned_off_or_overridable_fails(self) -> None:
        self.refused("ManagePackageVersionsCentrally", self.evaluated(ManagePackageVersionsCentrally=""))
        self.refused("CentralPackageVersionOverrideEnabled",
                     self.evaluated(CentralPackageVersionOverrideEnabled="true"))

    def test_another_pin_file_fails(self) -> None:
        other = os.path.join(self.root, "src", "Directory.Packages.props")
        self.refused("DirectoryPackagesPropsPath", self.evaluated(DirectoryPackagesPropsPath=other))
        self.refused("DirectoryPackagesPropsPath", self.evaluated(DirectoryPackagesPropsPath=""))

    def test_a_pinned_version_defined_or_changed_anywhere_else_fails(self) -> None:
        evaluated = self.evaluated()
        evaluated["Items"]["PackageVersion"][0]["DefiningProjectFullPath"] = os.path.join(self.root, "x.props")
        self.refused("is defined by", evaluated)
        evaluated = self.evaluated()
        evaluated["Items"]["PackageVersion"][0]["Version"] = "9.9.9"
        self.refused("not the pin file's '1.0.0'", evaluated)
        evaluated = self.evaluated()
        evaluated["Items"]["PackageVersion"] = []
        self.refused("missing from its evaluation", evaluated)

    def test_a_reference_carrying_a_version_of_its_own_fails(self) -> None:
        for kind in ("PackageReference", "PackageDownload"):
            for name in ("Version", "VersionOverride"):
                with self.subTest(kind=kind, name=name):
                    evaluated = self.evaluated()
                    evaluated["Items"][kind] = [{"Identity": "Anthropic", name: "9.9.9"}]
                    self.refused(name, evaluated)

    def test_only_the_sdks_own_implicit_reference_may_carry_a_version(self) -> None:
        sdk_file = os.path.join(self.dotnet, "sdk", "x", "Microsoft.NET.Sdk.props")
        evaluated = self.evaluated()
        evaluated["Items"]["PackageReference"] = [{"Identity": "NETStandard.Library", "Version": "2.0.3",
                                                   "IsImplicitlyDefined": "true",
                                                   "DefiningProjectFullPath": sdk_file}]
        self.assertEqual(self.problems(evaluated), [])
        evaluated["Items"]["PackageReference"][0]["DefiningProjectFullPath"] = os.path.join(self.root, "A.csproj")
        self.refused("Version", evaluated)
        evaluated["Items"]["PackageReference"][0].update(DefiningProjectFullPath=sdk_file, IsImplicitlyDefined="")
        self.refused("Version", evaluated)


def _dotnet_runs(where: Path) -> bool:
    if shutil.which("dotnet") is None:
        return False
    return subprocess.run(["dotnet", "--version"], cwd=where, capture_output=True, check=False).returncode == 0


PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
{properties}
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Anthropic" />
  </ItemGroup>
</Project>
"""


class ALiveEvaluation(unittest.TestCase):
    """MSBuild itself, on a repository built for the case: what the unit
    cases above assume about its output is held to the real thing."""

    def tree(self, **projects: str) -> Path:
        scratch = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(scratch.cleanup)
        base = Path(os.path.realpath(scratch.name))
        if not _dotnet_runs(base):
            self.skipTest("no .NET SDK runs here")
        (base / "outside.targets").write_text("<Project />", encoding="utf-8")
        root = base / "repo"
        root.mkdir()
        # Each stops MSBuild's walk upward at the repository's root.
        (root / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
        (root / "Directory.Build.targets").write_text("<Project />", encoding="utf-8")
        (root / "Directory.Packages.props").write_text(PIN, encoding="utf-8")
        for name, properties in projects.items():
            (root / name).mkdir()
            (root / name / f"{name}.csproj").write_text(PROJECT.format(properties=properties), encoding="utf-8")
        with cwd(root):
            run("git", "init", "-q")
            run("git", "add", ".")
        return root

    def test_a_repository_that_imports_nothing_from_outside_passes(self) -> None:
        root = self.tree(A="")
        out = io.StringIO()
        with cwd(root), contextlib.redirect_stdout(out):
            self.assertEqual(main([]), 0, out.getvalue())

    def test_a_built_import_path_and_one_only_release_takes_fail(self) -> None:
        # #562: the path is built from another property, which only
        # evaluation expands; B's import is under a Release condition, which
        # an evaluation with no configuration never sees.
        root = self.tree(
            A="    <Up>$(MSBuildThisFileDirectory)..\\..</Up>\n"
              "    <CustomAfterMicrosoftCommonTargets>$(Up)\\outside.targets</CustomAfterMicrosoftCommonTargets>",
            B="    <CustomAfterMicrosoftCommonTargets Condition=\"'$(Configuration)' == 'Release'\">"
              "$(MSBuildThisFileDirectory)..\\..\\outside.targets</CustomAfterMicrosoftCommonTargets>")
        out = io.StringIO()
        with cwd(root), contextlib.redirect_stdout(out):
            self.assertEqual(main([]), 1)
        errors = [line for line in out.getvalue().splitlines() if "outside.targets" in line]
        self.assertEqual(len(errors), 3, out.getvalue())
        self.assertTrue(any("A/A.csproj (no configuration)" in line for line in errors), errors)
        self.assertTrue(any("A/A.csproj (Release)" in line for line in errors), errors)
        self.assertTrue(any("B/B.csproj (Release)" in line for line in errors), errors)


class TheCiWiring(unittest.TestCase):
    # A gate nothing runs reads exactly like one that passes.

    def test_the_host_job_evaluates_on_both_legs_before_it_builds(self) -> None:
        # A condition can key an import on the operating system, so neither
        # leg's evaluation stands for the other's.
        block = job("host")
        step = "- run: ${{ runner.os == 'Windows' && 'python' || 'python3' }} .github/pin-gate/evaluate.py"
        self.assertIn(step, block)
        self.assertLess(block.index(step), block.index("dotnet build"))
        # `- run:` opens the step, so no `if:` precedes it; none may follow.
        after = block[block.index(step) + len(step):].split("\n", 2)[1]
        self.assertNotIn("if:", after)

    def test_the_commits_job_evaluates_each_commit_before_building_it(self) -> None:
        block = job("commits")
        self.assertIn('cp .github/pin-gate/evaluate.py "$RUNNER_TEMP/evaluate.py"', block)
        loop = block[block.index("for sha in"):]
        gate = 'python3 "$RUNNER_TEMP/evaluate.py"'
        self.assertIn(gate, loop)
        self.assertLess(loop.index("git clean"), loop.index(gate))
        self.assertLess(loop.index(gate), loop.index("dotnet build"))


if __name__ == "__main__":
    unittest.main()
