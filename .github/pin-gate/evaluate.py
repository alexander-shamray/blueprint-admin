#!/usr/bin/env python3
"""Every tracked project, as MSBuild evaluates it, imports nothing from outside the repository and the .NET install, and restores by the pin file alone.
The rules are .github/pin-gate/README.md's, *What the evaluation refuses*; run it as
py -3.12 .github/pin-gate/evaluate.py from the root of a tree that has not been restored.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from xml.parsers import expat

PIN_FILE = "Directory.Packages.props"

# Every tracked project is evaluated, not only the solution's: pin_gate.py
# holds a ProjectReference to a literal path to a tracked *proj, so this is
# every project a build can reach.
PROJECT = re.compile(r"\.[a-z]*proj$", re.IGNORECASE)

# The global properties a build hands each project: `dotnet build` with no
# configuration (`smoke`) and with Release (`host`, `commits`). A condition
# can key an import on either, so each is evaluated.
CONFIGURATIONS: tuple[str | None, ...] = (None, "Release")

# A feed and NuGet's settings come from the root nuget.config alone, so a
# project's evaluated value of each is empty.
FEEDS = ("RestoreSources", "RestoreAdditionalProjectSources", "RestoreConfigFile",
         "RestoreRootConfigDirectory")
# The folders restore reads packages from or writes them to: each entry lies
# inside the repository or the .NET install, which names its own
# NuGetFallbackFolder.
FOLDERS = ("RestorePackagesPath", "RestoreFallbackFolders", "RestoreAdditionalProjectFallbackFolders")
SETTINGS = {"ManagePackageVersionsCentrally": "true", "CentralPackageVersionOverrideEnabled": "false"}
PIN_PATH = "DirectoryPackagesPropsPath"
# Reserved, so no project can set it: the SDK MSBuild actually ran from, and
# so the check that the .NET install this gate found is that one.
SDK_PATH = "MSBuildBinPath"
# What restore writes. Once it exists, the project imports the restored
# packages' own props from the NuGet cache, outside both bounds, so the gate
# says the tree was restored rather than naming each of those.
ASSETS_FILE = "ProjectAssetsFile"
PROPERTIES = (*FEEDS, *FOLDERS, *SETTINGS, PIN_PATH, SDK_PATH, ASSETS_FILE)

# Items that carry a pinned version, which only the pin file defines, and
# those that name a package, which carry none of their own.
PIN_ITEMS = ("PackageVersion", "GlobalPackageReference")
REFERENCE_ITEMS = ("PackageReference", "PackageDownload")
VERSION_METADATA = ("Version", "VersionOverride")


class EvaluationError(RuntimeError):
    """MSBuild did not evaluate a project, so there is nothing to have passed."""


def _parser() -> expat.XMLParserType:
    """An expat parser that refuses a DOCTYPE, so no entity is ever expanded;
    pin_gate.py refuses one in any tracked file, and MSBuild writes none."""
    parser = expat.ParserCreate()

    def refuse(*_: object) -> None:
        raise EvaluationError("a DOCTYPE, which the gate does not expand")

    parser.StartDoctypeDeclHandler = refuse
    return parser


def imports(preprocessed: str) -> list[str]:
    """Every file `-preprocess` says it imported, from the banner it writes
    around each import. The output is read as XML, comments and all, so no
    text inside a project (a CDATA section holding `<!--`) can open a
    comment that swallows a banner."""
    found: list[str] = []

    def comment(text: str) -> None:
        lines = [line.strip() for line in text.strip().splitlines()]
        if len(lines) < 3 or set(lines[0]) != {"="} or set(lines[-1]) != {"="}:
            return
        body = [line for line in lines[1:-1] if line]
        if body and body[0].startswith("<Import "):
            found.append(body[-1])

    parser = _parser()
    parser.CommentHandler = comment
    try:
        parser.Parse(preprocessed, True)
    except expat.ExpatError as error:
        raise EvaluationError(f"the preprocessed project does not parse: {error}") from error
    return found


def pin_versions(data: bytes | str) -> dict[tuple[str, str], str]:
    """The pin file's literal versions, by item type and package, keyed
    without regard to case as MSBuild reads both."""
    versions: dict[tuple[str, str], str] = {}

    def start(tag: str, attrs: dict[str, str]) -> None:
        kind = tag.rsplit("}", 1)[-1]
        if kind in PIN_ITEMS and "Include" in attrs:
            versions[(kind.lower(), attrs["Include"].lower())] = attrs.get("Version", "")

    parser = _parser()
    parser.StartElementHandler = start
    try:
        parser.Parse(data, True)
    except expat.ExpatError as error:
        raise EvaluationError(f"{PIN_FILE} does not parse: {error}") from error
    return versions


def _canonical(path: str) -> str:
    return os.path.normcase(os.path.realpath(path))


def inside(path: str, root: str) -> bool:
    if not path:
        return False
    path, root = _canonical(path), _canonical(root)
    try:
        return os.path.commonpath([path, root]) == root
    except ValueError:  # another drive, or a relative path against an absolute one
        return False


def judge(project: str, configuration: str | None, evaluated: dict, imported: list[str], *,
          root: str, dotnet: str, pinned: dict[tuple[str, str], str]) -> list[str]:
    """What is wrong with one evaluation of one project, a repository path,
    as messages naming it and the configuration."""
    where = f"{project} ({configuration or 'no configuration'})"
    problems: list[str] = []
    properties: dict[str, str] = evaluated.get("Properties", {})
    items: dict[str, list[dict[str, str]]] = evaluated.get("Items", {})
    pin_file = os.path.join(root, PIN_FILE)

    def allowed(path: str) -> bool:
        return inside(path, root) or inside(path, dotnet)

    for path in imported:
        if not allowed(path):
            problems.append(f"{where}: imports {path}, outside the repository and the .NET install")
    for name in FEEDS:
        if properties.get(name, ""):
            problems.append(f"{where}: {name} evaluates to {properties[name]!r}; "
                            "a feed and NuGet's settings come from the root nuget.config alone")
    for name in FOLDERS:
        for entry in filter(None, (part.strip() for part in properties.get(name, "").split(";"))):
            # A relative entry is NuGet's to resolve against the project; it
            # is judged as though it were, so climbing out is caught too.
            if not allowed(os.path.join(root, os.path.dirname(project), entry)):
                problems.append(f"{where}: {name} names {entry}, outside the repository and the .NET install")
    for name, value in SETTINGS.items():
        if properties.get(name, "").lower() != value:
            problems.append(f"{where}: {name} evaluates to {properties.get(name, '')!r}, not {value}")
    if not properties.get(PIN_PATH) or _canonical(properties[PIN_PATH]) != _canonical(pin_file):
        problems.append(f"{where}: {PIN_PATH} evaluates to {properties.get(PIN_PATH, '')!r}, "
                        f"not the root {PIN_FILE}")

    seen: set[tuple[str, str]] = set()
    for kind in PIN_ITEMS:
        for item in items.get(kind, []):
            key = (kind.lower(), item.get("Identity", "").lower())
            seen.add(key)
            definer = item.get("DefiningProjectFullPath", "")
            if not definer or _canonical(definer) != _canonical(pin_file):
                problems.append(f"{where}: {kind} {item.get('Identity')} is defined by {definer!r}, "
                                f"not the root {PIN_FILE}")
            elif item.get("Version", "") != pinned.get(key):
                problems.append(f"{where}: {kind} {item.get('Identity')} evaluates to version "
                                f"{item.get('Version', '')!r}, not the pin file's {pinned.get(key)!r}")
    for kind, name in sorted(set(pinned) - seen):
        problems.append(f"{where}: the pin file's {kind} {name} is missing from its evaluation")
    for kind in REFERENCE_ITEMS:
        for item in items.get(kind, []):
            # The SDK's own implicit references (NETStandard.Library), whose
            # versions the global.json pin chooses. pin_gate.py refuses the
            # Update, the item definition and the built name by which a
            # project would change one.
            if (item.get("IsImplicitlyDefined", "").lower() == "true"
                    and inside(item.get("DefiningProjectFullPath", ""), dotnet)):
                continue
            for name in VERSION_METADATA:
                if item.get(name, ""):
                    problems.append(f"{where}: {kind} {item.get('Identity')} evaluates with "
                                    f"{name} {item[name]!r}; only the pin file chooses a version")
    return problems


def _dotnet(*args: str) -> str:
    done = subprocess.run(["dotnet", *args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", check=False,
                          env={**os.environ, "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    if done.returncode != 0:
        raise EvaluationError(f"dotnet {' '.join(args)}: {(done.stdout + done.stderr).strip()}")
    return done.stdout


def evaluate(project: str, configuration: str | None, scratch: str) -> tuple[dict, list[str]]:
    """One project's properties, package items and imports, as MSBuild evaluates them."""
    common = [project, "-nologo", "-nodeReuse:false"]
    if configuration:
        common.append(f"-property:Configuration={configuration}")
    asked = [f"-getProperty:{name}" for name in PROPERTIES]
    asked += [f"-getItem:{kind}" for kind in (*PIN_ITEMS, *REFERENCE_ITEMS)]
    output = _dotnet("msbuild", *common, *asked)
    try:
        evaluated = json.loads(output)
    except json.JSONDecodeError as error:
        raise EvaluationError(f"{project}: MSBuild's evaluation is not JSON: {error}: {output[:200]!r}") from error
    out = os.path.join(scratch, "preprocessed.xml")
    _dotnet("msbuild", *common, f"-preprocess:{out}")
    with open(out, encoding="utf-8-sig") as handle:
        return evaluated, imports(handle.read())


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise EvaluationError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def main(argv: list[str]) -> int:
    if argv:
        print(__doc__.splitlines()[0], file=sys.stderr)
        print("it takes no arguments: it evaluates the tree it is run in", file=sys.stderr)
        return 2
    try:
        root = _git("rev-parse", "--show-toplevel").strip()
        paths = sorted(p for p in _git("ls-files", "-z").split("\0") if p)
        found = shutil.which("dotnet")
        if found is None:
            raise EvaluationError("dotnet is not on PATH, so nothing was evaluated")
        dotnet = os.path.dirname(os.path.realpath(found))
        with open(os.path.join(root, PIN_FILE), "rb") as handle:
            pinned = pin_versions(handle.read())
        projects = [p for p in paths if PROJECT.search(p)]
        problems: list[str] = []
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as scratch:
            for project in projects:
                for configuration in CONFIGURATIONS:
                    evaluated, imported = evaluate(os.path.join(root, project), configuration, scratch)
                    assets = evaluated.get("Properties", {}).get(ASSETS_FILE, "")
                    if assets and os.path.exists(assets):
                        raise EvaluationError(f"{project}: {assets} exists, so the tree was restored; "
                                              "evaluate a clean checkout, as CI does before it restores")
                    sdk = evaluated.get("Properties", {}).get(SDK_PATH, "")
                    if not inside(sdk, dotnet):
                        raise EvaluationError(f"{project}: MSBuild ran from {sdk!r}, not the .NET install "
                                              f"on PATH ({dotnet}), so that install's bounds are unknown")
                    problems += judge(project, configuration, evaluated, imported,
                                      root=root, dotnet=dotnet, pinned=pinned)
    except (EvaluationError, OSError) as error:
        print(f"::error::{error}")
        return 2
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"{len(projects)} projects evaluated under {len(CONFIGURATIONS)} configurations; none imports "
          "from outside the repository and the .NET install, or restores past the pin file")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
