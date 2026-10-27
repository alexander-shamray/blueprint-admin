#!/usr/bin/env python3
"""Negative cases for the pin gate, each beside a positive control, and the
wiring that makes CI run it."""

from __future__ import annotations

import contextlib
import io
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

from pin_gate import PIN_FILE, check, check_other, check_pin_file, collect, main

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
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Anthropic" Version="1.0.0" />
    <GlobalPackageReference Include="Analyzer" Version="1.0.0" />
  </ItemGroup>
</Project>
"""


def project(body: str, sdk: str = "Microsoft.NET.Sdk") -> str:
    return f'<Project Sdk="{sdk}">\n{body}\n</Project>\n'


def refused(test: unittest.TestCase, text: str, needle: str) -> None:
    problems = check("src/X/X.csproj", text)
    test.assertEqual(len(problems), 1, problems)
    test.assertIn(needle.lower(), problems[0].lower())


def pin_problems(text: str) -> list[str]:
    return check_pin_file(text) + check(PIN_FILE, text, pin=True)


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

    def test_names_are_read_without_regard_to_case_as_msbuild_reads_them(self) -> None:
        # R1 of #550: NuGet honours each of these spellings.
        for body, needle in (
            ("<PropertyGroup><managePackageVersionsCentrally>false</managePackageVersionsCentrally>"
             "</PropertyGroup>", "managepackageversionscentrally"),
            ("<PropertyGroup><restoreSources>https://x</restoreSources></PropertyGroup>", "restoresources"),
            ('<ItemGroup><packageDownload Include="A" Version="[1.0.0]" /></ItemGroup>', "packagedownload"),
            ('<ItemGroup><PackageReference Include="A" versionOverride="9" /></ItemGroup>', "versionoverride"),
            ('<ItemGroup><packagereference Include="A"><VERSION>9</VERSION></packagereference></ItemGroup>',
             "sets version"),
        ):
            with self.subTest(needle=needle):
                refused(self, project(body), needle)

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

    def test_properties_that_import_a_file_by_path_fail(self) -> None:
        # Commit security review of #550: each imports a tracked file with no
        # <Import> element in sight.
        for name in ("CustomAfterMicrosoftCommonTargets", "CustomBeforeMicrosoftCommonProps",
                     "DirectoryBuildTargetsPath", "ImportDirectoryBuildProps",
                     "ImportProjectExtensionProps", "MSBuildProjectExtensionsPath",
                     "ForceImportBeforeCppTargets", "ImportByWildcardBeforeMicrosoftCommonTargets",
                     # R1 of round 3: each reaches a file of any name.
                     "MSBuildUserExtensionsPath", "MSBuildExtensionsPath", "LanguageTargets",
                     "ImportUserLocationsByWildcardBeforeMicrosoftCommonProps",
                     "BeforeMicrosoftNETSdkTargets", "AfterMicrosoftNETSdkTargets"):
            with self.subTest(name=name):
                refused(self, project(f"<PropertyGroup><{name}>x</{name}></PropertyGroup>"), f"<{name}>")

    def test_a_runtime_pack_version_fails(self) -> None:
        # R4 of #550: restore downloads the pack these name.
        for name in ("RuntimeFrameworkVersion", "WindowsSdkPackageVersion",
                     "MicrosoftNETCoreAppRefPackageVersion"):
            with self.subTest(name=name):
                refused(self, project(f"<PropertyGroup><{name}>9.0.0</{name}></PropertyGroup>"), name)
        for item in ("KnownFrameworkReference", "KnownAppHostPack", "KnownILLinkPack"):
            with self.subTest(item=item):
                refused(self, project(f'<ItemGroup><{item} Update="Microsoft.NETCore.App" '
                                      'LatestRuntimeFrameworkVersion="9.0.0" /></ItemGroup>'), item)

    def test_code_in_a_project_file_fails(self) -> None:
        # Inside a target, CreateProperty or <Output PropertyName> sets what
        # no static rule sees; no project here carries one.
        refused(self, project('<Target Name="T" BeforeTargets="_GenerateRestoreProjectSpec">'
                              '<CreateProperty Value="https://x"><Output TaskParameter="Value" '
                              'PropertyName="RestoreSources" /></CreateProperty></Target>'), "<Target>")
        refused(self, project('<UsingTask TaskName="T" AssemblyFile="t.dll" />'), "<UsingTask>")

    def test_a_project_reference_to_a_file_not_named_as_a_project_fails(self) -> None:
        # Commit security review of #550: a reference reaches a file by any
        # name, and only a *proj is certain to have been read.
        # And one MSBuild expands may become another name, or several, after
        # the gate has read it.
        for include in ("..\\lib\\x.xml", "$(Elsewhere)", "a.csproj;b.txt", "$(X).csproj",
                        "src/*.csproj", "@(Refs)", "%(Identity).csproj"):
            with self.subTest(include=include):
                refused(self, project(f'<ItemGroup><ProjectReference Include="{include}" /></ItemGroup>'),
                        "not a literal *proj path")

    def test_a_project_reference_rooted_outside_the_repository_fails(self) -> None:
        # Commit security review of #550: a drive, a root or a share names
        # MSBuild the gate never reads, a share over the network.
        for include in ("C:\\elsewhere\\a.csproj", "\\\\host\\share\\a.csproj", "/abs/a.csproj"):
            with self.subTest(include=include):
                refused(self, project(f'<ItemGroup><ProjectReference Include="{include}" /></ItemGroup>'),
                        "rooted outside")

    def test_msbuilds_underscored_inputs_and_named_imports_fail(self) -> None:
        # R1 and R3 of round 5: the underscored inputs NuGet and the SDK build
        # the pin file's and Directory.Build's paths from, and a property
        # named for a file the common targets import.
        for name in ("_DirectoryPackagesPropsBasePath", "_DirectoryPackagesPropsFile",
                     "_DirectoryBuildTargetsBasePath", "__RestoreSources",
                     "CodeAnalysisTargets", "ReportingServicesTargets", "SomeImportProps"):
            with self.subTest(name=name):
                refused(self, project(f"<PropertyGroup><{name}>x</{name}></PropertyGroup>"), f"<{name}>")

    def test_a_literal_value_outside_the_repository_fails(self) -> None:
        # R3 of round 5: a drive, a root, a share or a climb past the top.
        for value in ("C:\\elsewhere", "\\\\host\\share\\x", "/etc/x", "..\\..\\..\\x", "ok;..\\..\\..\\x"):
            with self.subTest(value=value):
                refused(self, project(f"<PropertyGroup><OutputPath>{value}</OutputPath></PropertyGroup>"),
                        "outside the repository")

    def test_a_value_inside_or_built_from_properties_passes(self) -> None:
        # The built one is the residual the README names.
        for value in ("..\\shared", "bin", "$(MSBuildThisFileDirectory)artifacts", "$(NoWarn);CA1707",
                      "https://example.com/x", "net10.0"):
            with self.subTest(value=value):
                self.assertEqual(check("src/X/X.csproj", project(
                    f"<PropertyGroup><OutputPath>{value}</OutputPath></PropertyGroup>")), [])

    def test_a_package_reference_copying_another_items_metadata_fails(self) -> None:
        # R4 of round 5: the transform carries Version across unread.
        for attr, value in (("Include", "@(Pinned)"), ("Update", "%(Identity)")):
            with self.subTest(attr=attr):
                refused(self, project(f'<ItemGroup><PackageReference {attr}="{value}" /></ItemGroup>'),
                        "copies another item's metadata")

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
        # R4 of #550: Functions shares the prefix and is a NuGet package.
        for sdk in ("Microsoft.NET.Sdk/10.0.100", "Some.Sdk", "Microsoft.NET.Sdk;Some.Sdk/1.0",
                    "Microsoft.NET.Sdk.Functions"):
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
    def test_the_settings_and_items_it_exists_to_hold_pass(self) -> None:
        self.assertEqual(pin_problems(PIN), [])

    def test_overrides_left_on_fail(self) -> None:
        for value in ("true", ""):
            with self.subTest(value=value):
                text = PIN.replace("<CentralPackageVersionOverrideEnabled>false",
                                   f"<CentralPackageVersionOverrideEnabled>{value}")
                self.assertEqual(len(pin_problems(text)), 1)

    def test_overrides_never_mentioned_fail(self) -> None:
        text = re.sub(r"\s*<CentralPackageVersionOverrideEnabled>.*?</CentralPackageVersionOverrideEnabled>",
                      "", PIN)
        self.assertEqual(len(pin_problems(text)), 1)

    def test_central_management_off_fails(self) -> None:
        text = PIN.replace("<ManagePackageVersionsCentrally>true", "<ManagePackageVersionsCentrally>false")
        self.assertEqual(len(pin_problems(text)), 1)

    def test_a_setting_under_a_condition_or_set_twice_fails(self) -> None:
        # The value read here would be one MSBuild may never evaluate.
        conditional = PIN.replace("<CentralPackageVersionOverrideEnabled>",
                                  "<CentralPackageVersionOverrideEnabled Condition=\"'$(X)'==''\">")
        group = PIN.replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(X)'==''\">")
        twice = PIN.replace("</PropertyGroup>",
                            "<CentralPackageVersionOverrideEnabled>true</CentralPackageVersionOverrideEnabled>"
                            "</PropertyGroup>")
        # R3 of #550: a definition inside a <Choose> or a target is not the
        # value MSBuild evaluates either.
        override = "<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>"
        moved = PIN.replace(override, "")
        chosen = moved.replace("<ItemGroup>", f"<Choose><When Condition=\"'$(X)'==''\"><PropertyGroup>"
                                              f"{override}</PropertyGroup></When></Choose><ItemGroup>", 1)
        targeted = moved.replace("<ItemGroup>", f"<Target Name=\"T\"><PropertyGroup>{override}"
                                                f"</PropertyGroup></Target><ItemGroup>", 1)
        for name, text in (("condition", conditional), ("group", group), ("twice", twice),
                           ("choose", chosen), ("target", targeted)):
            with self.subTest(name):
                self.assertTrue(pin_problems(text))

    def test_its_feed_and_import_are_refused_as_anywhere_else(self) -> None:
        for extra in ("<PropertyGroup><RestoreSources>https://x</RestoreSources></PropertyGroup>",
                      '<Import Project="other.props" />',
                      '<ItemGroup><PackageDownload Include="A" Version="[1.0.0]" /></ItemGroup>'):
            with self.subTest(extra=extra):
                self.assertEqual(len(pin_problems(PIN.replace("</Project>", extra + "</Project>"))), 1)


class OtherRestoreInputs(unittest.TestCase):
    # R3 of #550: what MSBuild and NuGet read without any project naming it.

    def test_a_response_file_fails(self) -> None:
        for path in ("Directory.Build.rsp", "src/MSBuild.rsp"):
            with self.subTest(path=path):
                self.assertTrue(check_other(path, "-p:RestoreSources=https://x"))

    def test_only_the_one_solution_naming_only_projects_passes(self) -> None:
        good = '<Solution><Folder Name="/src/"><Project Path="src/A/A.csproj" /></Folder></Solution>'
        self.assertEqual(check_other("BlueprintAdmin.slnx", good), [])
        self.assertTrue(check_other("BlueprintAdmin.slnx", good.replace("A.csproj", "x.xml")))
        for path in ("Other.slnx", "src/Old.sln"):
            with self.subTest(path=path):
                self.assertTrue(check_other(path, good))
        # Each entry must be a tracked file, matched as Windows matches it.
        self.assertEqual(check_other("BlueprintAdmin.slnx", good, frozenset({"src/a/a.csproj"})), [])
        self.assertTrue(check_other("BlueprintAdmin.slnx", good, frozenset({"src/b/b.csproj"})))
        self.assertTrue(check_other("BlueprintAdmin.slnx", good.replace("src/A", "../out"),
                                    frozenset({"src/a/a.csproj"})))

    def test_a_pin_file_below_the_root_fails(self) -> None:
        # R2 of round 5: NuGet takes the nearest one above a project.
        for path in ("src/Admin.Host/Directory.Packages.props", "tests/directory.packages.props"):
            with self.subTest(path=path):
                self.assertTrue(check_other(path, "<Project />"))

    def test_only_the_root_feed_list_passes(self) -> None:
        self.assertEqual(check_other("nuget.config", "<configuration />"), [])
        for path in ("src/nuget.config", "tests/X/NuGet.Config"):
            with self.subTest(path=path):
                self.assertTrue(check_other(path, "<configuration />"))

    def test_an_sdk_pin_that_fetches_msbuild_sdks_fails(self) -> None:
        self.assertEqual(check_other("global.json", '{"sdk": {"version": "10.0.100"}}'), [])
        self.assertTrue(check_other("global.json", '{"msbuild-sdks": {"Some.Sdk": "1.0.0"}}'))
        self.assertTrue(check_other("src/global.json", '{"MSBuild-Sdks": {"Some.Sdk": "1.0.0"}}'))
        self.assertTrue(check_other("global.json", "{"))


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

    def test_only_tracked_restore_inputs_are_read(self) -> None:
        with tempfile.TemporaryDirectory() as scratch, inside(Path(scratch)):
            run("git", "init", "-q")
            Path(PIN_FILE).write_text(PIN, encoding="utf-8")
            Path("src").mkdir()
            Path("src/A.csproj").write_text(PLAIN, encoding="utf-8")
            # PLAIN's ProjectReference, which must resolve to a tracked file.
            Path("Admin.Assistant").mkdir()
            Path("Admin.Assistant/Admin.Assistant.csproj").write_text("<Project />", encoding="utf-8")
            Path("src/A.csproj.user").write_text("<Project />", encoding="utf-8")
            Path("src/B.targets").write_text("<Project />", encoding="utf-8")
            Path("src/notes.md").write_text("VersionOverride", encoding="utf-8")
            Path("global.json").write_text("{}", encoding="utf-8")
            # R2 of #550: a plain .proj; R1: a project by any other name,
            # which a ProjectReference or an import path reaches.
            Path("dirs.proj").write_text("<Project />", encoding="utf-8")
            Path("src/lib.xml").write_text('<?xml version="1.0"?>\n<!-- a -->\n<Project>\n</Project>',
                                           encoding="utf-8")
            # Round 3: the root is found by parsing, so a processing
            # instruction holding `>`, a prefix, or UTF-16 with no byte-order
            # mark does not hide it; an HTML page is no import.
            Path("src/pi.xml").write_text('<?xml version="1.0"?><?other a>b?>\n<Project />', encoding="utf-8")
            Path("src/prefixed.xml").write_text(
                '<m:Project xmlns:m="http://schemas.microsoft.com/developer/msbuild/2003" />', encoding="utf-8")
            Path("src/narrow.xml").write_bytes('<?xml version="1.0" encoding="UTF-16"?><Project />'
                                               .encode("utf-16-le"))
            Path("src/page.html").write_text("<!doctype html>\n<html><body><br></body></html>",
                                             encoding="utf-8")
            Path("src/other.xml").write_text("<configuration />", encoding="utf-8")
            Path("src/wide.csproj").write_bytes(PLAIN.encode("utf-16"))
            Path("src/logo.png").write_bytes(b"\x89PNG\r\n\x1a\n\xff\xfe\x00")
            run("git", "add", ".")
            Path("src/Untracked.csproj").write_text(project('<Import Project="x" />'), encoding="utf-8")
            self.assertEqual(collect(), ["Admin.Assistant/Admin.Assistant.csproj", PIN_FILE, "dirs.proj",
                                         "global.json", "src/A.csproj",
                                         "src/A.csproj.user", "src/B.targets", "src/lib.xml",
                                         "src/narrow.xml", "src/pi.xml", "src/prefixed.xml",
                                         "src/wide.csproj"])
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main([]), 0)

    def test_a_tracked_override_fails_the_run(self) -> None:
        with tempfile.TemporaryDirectory() as scratch, inside(Path(scratch)):
            run("git", "init", "-q")
            Path(PIN_FILE).write_text(PIN, encoding="utf-8")
            Path("A.csproj.user").write_text(
                '<Project><ItemGroup><PackageReference Update="A" VersionOverride="9" /></ItemGroup></Project>',
                encoding="utf-8")
            Path("Directory.Build.rsp").write_text("-p:RestoreSources=https://x", encoding="utf-8")
            Path("after.anything").write_text(
                '<Project><PropertyGroup><CentralPackageVersionOverrideEnabled>true'
                '</CentralPackageVersionOverrideEnabled></PropertyGroup></Project>', encoding="utf-8")
            Path("B.csproj").write_bytes(b"\xff\x00\xfe")
            # Read as an import and refused: UTF-32, which expat cannot
            # decode, and a DOCTYPE'd root the gate cannot name unexpanded.
            Path("wide32.xml").write_bytes("<Project />".encode("utf-32"))
            Path("dtd.xml").write_text('<!DOCTYPE x [<!ENTITY e "y">]><Project />', encoding="utf-8")
            # Round 4, R6: an encoding XmlReader knows and expat does not.
            Path("jis.xml").write_bytes(b'<?xml version="1.0" encoding="shift_jis"?><Project />')
            Path("unicode.xml").write_bytes('<?xml version="1.0" encoding="unicode"?><Project />'
                                            .encode("utf-16"))
            # A reference to a file outside the repository, or untracked.
            Path("C.csproj").write_text(project(
                '<ItemGroup><ProjectReference Include="..\\outside\\x.csproj" /></ItemGroup>'),
                encoding="utf-8")
            run("git", "add", ".")
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(main([]), 1)
            for path in ("A.csproj.user", "Directory.Build.rsp", "after.anything", "B.csproj",
                         "wide32.xml", "dtd.xml", "jis.xml", "unicode.xml", "C.csproj"):
                with self.subTest(path=path):
                    self.assertIn(f"::error::{path}", out.getvalue())

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
    # #547: a gate nothing runs reads exactly like one that passes.

    def gate_before_build(self, block: str, gate: str) -> None:
        self.assertIn(gate, block)
        self.assertIn("dotnet build", block)
        self.assertLess(block.index(gate), block.index("dotnet build"))

    def test_the_host_job_runs_the_gate_before_it_builds(self) -> None:
        # `host (ubuntu-latest)` is a required check, so the gate holds a
        # merge outside /ship too, with no ruleset change.
        block = job("host")
        self.gate_before_build(block, "python3 .github/pin-gate/pin_gate.py")
        self.assertIn("runner.os == 'Linux'", block)

    def test_the_commits_job_runs_the_gate_on_each_commit_before_building_it(self) -> None:
        # Each commit before the head is what a later bisect restores.
        block = job("commits")
        self.assertIn('cp .github/pin-gate/pin_gate.py "$RUNNER_TEMP/pin_gate.py"', block)
        loop = block[block.index("for sha in"):]
        self.gate_before_build(loop, 'python3 "$RUNNER_TEMP/pin_gate.py"')


if __name__ == "__main__":
    unittest.main()
