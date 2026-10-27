#!/usr/bin/env python3
"""Negative cases for the pin gate, each beside a positive control, and the
wiring that makes CI run it before anything restores."""

from __future__ import annotations

import contextlib
import io
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

from pin_gate import PIN_FILE, check, check_pin_file, collect, main

ROOT = Path(__file__).resolve().parents[2]

PLAIN = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Anthropic" />
    <ProjectReference Include="..\\Admin.Assistant\\Admin.Assistant.csproj" />
  </ItemGroup>
</Project>
"""

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


def project(body: str, sdk: str = "Microsoft.NET.Sdk") -> str:
    return f'<Project Sdk="{sdk}">\n{body}\n</Project>\n'


def refused(test: unittest.TestCase, text: str, needle: str) -> None:
    problems = check("src/X/X.csproj", text)
    test.assertEqual(len(problems), 1, problems)
    test.assertIn(needle, problems[0])


class AProjectFile(unittest.TestCase):
    def test_a_project_that_names_packages_without_versions_passes(self) -> None:
        self.assertEqual(check("src/X/X.csproj", PLAIN), [])

    def test_a_version_override_fails(self) -> None:
        refused(self, project('<ItemGroup><PackageReference Include="A" VersionOverride="9.9.9" /></ItemGroup>'),
                "VersionOverride")

    def test_an_inline_version_fails(self) -> None:
        refused(self, project('<ItemGroup><PackageReference Include="A" Version="9.9.9" /></ItemGroup>'),
                "sets Version")

    def test_a_version_as_metadata_fails(self) -> None:
        refused(self, project('<ItemGroup><PackageReference Update="A"><Version>9.9.9</Version>'
                              '</PackageReference></ItemGroup>'), "PackageReference A sets Version")

    def test_a_version_given_every_reference_by_an_item_definition_fails(self) -> None:
        refused(self, project("<ItemDefinitionGroup><PackageReference><VersionOverride>9.9.9</VersionOverride>"
                              "</PackageReference></ItemDefinitionGroup>"), "VersionOverride")

    def test_an_old_style_namespaced_project_is_read_too(self) -> None:
        text = ('<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">'
                '<ItemGroup><PackageReference Include="A" VersionOverride="9" /></ItemGroup></Project>')
        refused(self, text, "VersionOverride")

    def test_a_comment_naming_the_attribute_passes(self) -> None:
        # Why the gate parses rather than greps: prose about the rule is not
        # the rule broken.
        self.assertEqual(check("src/X/X.csproj", project('<!-- never VersionOverride="1" -->')), [])

    def test_items_that_carry_their_own_version_fail(self) -> None:
        for item in ("PackageVersion", "GlobalPackageReference", "PackageDownload"):
            with self.subTest(item=item):
                refused(self, project(f'<ItemGroup><{item} Include="A" Version="[1.0.0]" /></ItemGroup>'),
                        f"<{item}>")

    def test_central_management_and_feed_properties_fail(self) -> None:
        for name in ("ManagePackageVersionsCentrally", "CentralPackageVersionOverrideEnabled",
                     "CentralPackageTransitivePinningEnabled", "RestoreSources",
                     "RestoreAdditionalProjectSources", "RestoreConfigFile", "NuGetAudit",
                     "DirectoryPackagesPropsPath", "ImportDirectoryPackagesProps"):
            with self.subTest(name=name):
                refused(self, project(f"<PropertyGroup><{name}>x</{name}></PropertyGroup>"), f"<{name}>")

    def test_a_property_set_inside_a_choose_is_read(self) -> None:
        refused(self, project("<Choose><When Condition=\"true\"><PropertyGroup>"
                              "<RestoreSources>https://x</RestoreSources></PropertyGroup></When></Choose>"),
                "<RestoreSources>")

    def test_an_ordinary_property_beside_them_passes(self) -> None:
        # The prefixes stop at their own names.
        self.assertEqual(check("src/X/X.csproj", project(
            "<PropertyGroup><RootNamespace>X</RootNamespace><Nullable>enable</Nullable></PropertyGroup>")), [])

    def test_an_import_fails(self) -> None:
        refused(self, project('<Import Project="..\\elsewhere.props" />'), "<Import>")

    def test_an_sdk_element_fails(self) -> None:
        refused(self, project('<Sdk Name="Some.Sdk" Version="1.0.0" />'), "<Sdk>")

    def test_a_versioned_or_foreign_project_sdk_fails(self) -> None:
        for sdk in ("Microsoft.NET.Sdk/10.0.100", "Some.Sdk", "Microsoft.NET.Sdk;Some.Sdk/1.0"):
            with self.subTest(sdk=sdk):
                self.assertTrue(check("src/X/X.csproj", project("", sdk=sdk)))

    def test_the_bundled_sdks_pass(self) -> None:
        for sdk in ("Microsoft.NET.Sdk", "Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Razor"):
            with self.subTest(sdk=sdk):
                self.assertEqual(check("src/X/X.csproj", project("", sdk=sdk)), [])

    def test_a_file_that_does_not_parse_fails_rather_than_passing_unread(self) -> None:
        refused(self, "<Project><ItemGroup></Project>", "not well-formed")

    def test_a_doctype_fails_before_anything_is_parsed(self) -> None:
        refused(self, '<!DOCTYPE p [<!ENTITY e "x">]><Project />', "DOCTYPE")


class ThePinFile(unittest.TestCase):
    def test_the_settings_the_gate_rests_on_pass(self) -> None:
        self.assertEqual(check_pin_file(PIN), [])

    def test_overrides_left_on_fail(self) -> None:
        for value in ("true", ""):
            with self.subTest(value=value):
                text = PIN.replace("<CentralPackageVersionOverrideEnabled>false",
                                   f"<CentralPackageVersionOverrideEnabled>{value}")
                self.assertEqual(len(check_pin_file(text)), 1)

    def test_overrides_never_mentioned_fail(self) -> None:
        text = re.sub(r"\s*<CentralPackageVersionOverrideEnabled>.*?</CentralPackageVersionOverrideEnabled>",
                      "", PIN)
        self.assertEqual(len(check_pin_file(text)), 1)

    def test_central_management_off_fails(self) -> None:
        text = PIN.replace("<ManagePackageVersionsCentrally>true", "<ManagePackageVersionsCentrally>false")
        self.assertEqual(len(check_pin_file(text)), 1)


@contextlib.contextmanager
def inside(path: Path):
    before = os.getcwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(before)


def run(*args: str) -> None:
    subprocess.run(args, check=True, capture_output=True)


class TheTree(unittest.TestCase):
    def test_this_repository_passes(self) -> None:
        # The tree the gate lands into is the first one it judges.
        with inside(ROOT), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(main([]), 0)

    def test_only_tracked_msbuild_files_are_read(self) -> None:
        with tempfile.TemporaryDirectory() as scratch, inside(Path(scratch)):
            run("git", "init", "-q")
            Path(PIN_FILE).write_text(PIN, encoding="utf-8")
            Path("src").mkdir()
            Path("src/A.csproj").write_text(PLAIN, encoding="utf-8")
            Path("src/B.targets").write_text("<Project />", encoding="utf-8")
            Path("src/notes.md").write_text("VersionOverride", encoding="utf-8")
            run("git", "add", ".")
            Path("src/Untracked.csproj").write_text(project('<Import Project="x" />'), encoding="utf-8")
            self.assertEqual(collect(), [PIN_FILE, "src/A.csproj", "src/B.targets"])
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main([]), 0)

    def test_a_tracked_override_fails_the_run(self) -> None:
        with tempfile.TemporaryDirectory() as scratch, inside(Path(scratch)):
            run("git", "init", "-q")
            Path(PIN_FILE).write_text(PIN, encoding="utf-8")
            Path("A.csproj").write_text(
                project('<ItemGroup><PackageReference Include="A" VersionOverride="9" /></ItemGroup>'),
                encoding="utf-8")
            run("git", "add", ".")
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(main([]), 1)
            self.assertIn("::error::A.csproj", out.getvalue())

    def test_a_tree_with_no_pin_file_fails(self) -> None:
        with tempfile.TemporaryDirectory() as scratch, inside(Path(scratch)):
            run("git", "init", "-q")
            Path("A.csproj").write_text(PLAIN, encoding="utf-8")
            run("git", "add", ".")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main([]), 1)


def job(name: str) -> str:
    """One job's block of ci.yml, from its key to the next job's."""
    text = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    match = re.search(rf"^  {name}:\n(.*?)(?=^  [a-z-]+:\n|\Z)", text, re.MULTILINE | re.DOTALL)
    assert match, f"ci.yml has no {name} job"
    return match.group(1)


class TheCiWiring(unittest.TestCase):
    # #547: a gate nothing runs reads exactly like one that passes. These hold
    # CI to running it before each restore it guards.

    def gate_before_build(self, block: str, gate: str) -> None:
        self.assertIn(gate, block)
        self.assertIn("dotnet build", block)
        self.assertLess(block.index(gate), block.index("dotnet build"))

    def test_the_host_job_runs_the_gate_before_it_restores(self) -> None:
        # `host (ubuntu-latest)` is a required check, so the gate holds a
        # merge outside /ship too, with no ruleset change.
        block = job("host")
        self.gate_before_build(block, "python3 .github/pin-gate/pin_gate.py")
        self.assertIn("runner.os == 'Linux'", block)

    def test_the_commits_job_runs_the_gate_on_each_commit_before_building_it(self) -> None:
        # Each commit before the head is restored there, and by anyone who
        # bisects onto it later.
        block = job("commits")
        self.assertIn('cp .github/pin-gate/pin_gate.py "$RUNNER_TEMP/pin_gate.py"', block)
        loop = block[block.index("for sha in"):]
        self.gate_before_build(loop, 'python3 "$RUNNER_TEMP/pin_gate.py"')


if __name__ == "__main__":
    unittest.main()
