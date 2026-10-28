#!/usr/bin/env python3
"""Every build a binary log records imported nothing from outside the repository, the .NET install and the packages it restored, and restored by the pin file and the root nuget.config alone.
The rules are .github/pin-gate/README.md's, *What the build's judge refuses*; run it as
py -3.12 .github/pin-gate/judge_build.py BINLOG... from the root of the tree that was built.
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
CONFIG = "nuget.config"

# Every tracked project must appear in the log: pin_gate.py holds a
# ProjectReference to a literal path to a tracked *proj, so one the build did
# not evaluate is one the gate never judged.
PROJECT = re.compile(r"\.[a-z]*proj$", re.IGNORECASE)

# MSBuild's ProjectImportedEventArgs, as its diagnostic file logger replays
# them, after the timestamp and node id it puts before some lines. A line
# naming an import in any other shape is refused rather than skipped: a path
# holding a quote would make the two halves ambiguous.
IMPORT = re.compile(r'^\s*(?:\d\d:\d\d:\d\d\.\d+\s+)?(?:\d+>)?'
                    r'Importing project "([^"]*)" into project "([^"]*)" at \(\d+,\d+\)\.\s*$')
MARKER = 'Importing project "'

# What restore writes beside its assets file, and so how the log names the
# directory holding it: Microsoft.Common.props imports both.
RESTORE_OUTPUT = (".nuget.g.props", ".nuget.g.targets")
ASSETS = "project.assets.json"

PIN_ITEMS = ("PackageVersion", "GlobalPackageReference")


class JudgeError(RuntimeError):
    """The log could not be read, so there is nothing to have passed."""


def _parser() -> expat.XMLParserType:
    """An expat parser that refuses a DOCTYPE, so no entity is ever expanded;
    pin_gate.py refuses one in any tracked file."""
    parser = expat.ParserCreate()

    def refuse(*_: object) -> None:
        raise JudgeError("a DOCTYPE, which the gate does not expand")

    parser.StartDoctypeDeclHandler = refuse
    return parser


def imports(lines) -> list[tuple[str, str]]:
    """Every (imported, importer) pair a replayed diagnostic log names, from
    restore's evaluations and the build's alike."""
    found: list[tuple[str, str]] = []
    for line in lines:
        if MARKER not in line:
            continue
        match = IMPORT.match(line)
        if match is None:
            raise JudgeError(f"a line naming an import the gate cannot read: {line.strip()[:300]!r}")
        found.append((match.group(1), match.group(2)))
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
        raise JudgeError(f"{PIN_FILE} does not parse: {error}") from error
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


def _source(value: str, base: str) -> str:
    """A package source as restore names it: a URL without its trailing
    slash and without regard to case, a folder as its canonical path."""
    value = value.strip()
    if "://" in value:
        return value.rstrip("/").lower()
    return _canonical(os.path.join(base, value))


def config_sources(data: bytes | str, base: str) -> set[str]:
    """The package sources the root nuget.config leaves standing, a `clear`
    dropping every one named before it."""
    sources: list[str] = []
    stack: list[str] = []

    def start(tag: str, attrs: dict[str, str]) -> None:
        if stack == ["configuration", "packageSources"]:
            if tag == "clear":
                sources.clear()
            elif tag == "add" and "value" in attrs:
                sources.append(_source(attrs["value"], base))
        stack.append(tag)

    def end(_: str) -> None:
        stack.pop()

    parser = _parser()
    parser.StartElementHandler = start
    parser.EndElementHandler = end
    try:
        parser.Parse(data, True)
    except expat.ExpatError as error:
        raise JudgeError(f"{CONFIG} does not parse: {error}") from error
    return set(sources)


def judge_assets(assets: dict, *, where: str, root: str, dotnet: str, config: str,
                 sources: set[str], pinned: dict[tuple[str, str], str]) -> list[str]:
    """What is wrong with what one project's restore says it read and
    resolved, as messages naming the project."""
    problems: list[str] = []
    restore: dict = assets.get("project", {}).get("restore", {})
    for name in ("centralPackageVersionsManagementEnabled", "centralPackageVersionOverrideDisabled"):
        if restore.get(name) is not True:
            problems.append(f"{where}: restore read {name} as {restore.get(name)!r}, not true")

    # Nearest first: the root's, then any above the checkout and the user's
    # and the machine's, none of which the repository writes.
    configs: list[str] = restore.get("configFilePaths", [])
    if not configs or _canonical(configs[0]) != _canonical(config):
        problems.append(f"{where}: restore's nearest NuGet config is {configs[0] if configs else None!r}, "
                        f"not the root {CONFIG}")
    for path in configs[1:]:
        if inside(path, root) and _canonical(path) != _canonical(config):
            problems.append(f"{where}: restore read the NuGet config {path}, inside the repository")

    # The SDK's own local sources (a Windows runner's library-packs) lie
    # inside the install.
    for source in restore.get("sources", {}):
        if _source(source, root) in sources or ("://" not in source and inside(source, dotnet)):
            continue
        problems.append(f"{where}: restore read the source {source}, which the root {CONFIG} does not name")

    # Never the repository: restore takes a package already extracted in a
    # packages or fallback folder as it finds it, so a folder inside the
    # tree would be package content the repository chose.
    folders = [*assets.get("packageFolders", {}), restore.get("packagesPath", ""),
               *restore.get("fallbackFolders", [])]
    for folder in dict.fromkeys(filter(None, folders)):
        if inside(folder, root):
            problems.append(f"{where}: restore took packages from {folder}, inside the repository")

    pins = {name: version for (kind, name), version in pinned.items() if kind == "packageversion"}
    global_references = {name for kind, name in pinned if kind == "globalpackagereference"}
    for framework, body in sorted(assets.get("project", {}).get("frameworks", {}).items()):
        central = {name.lower(): version for name, version in body.get("centralPackageVersions", {}).items()}
        if central != pins:
            changed = sorted(set(central.items()) ^ set(pins.items()))
            problems.append(f"{where} ({framework}): restore's central versions differ from the pin file's: "
                            + ", ".join(f"{name} {version}" for name, version in changed))
        for name, dependency in sorted(body.get("dependencies", {}).items()):
            # The SDK's implicit references (NETStandard.Library) take the
            # version the global.json pin chooses; pin_gate.py refuses the
            # metadata by which a project would add or change one.
            if (dependency.get("versionCentrallyManaged") is True or dependency.get("autoReferenced") is True
                    or name.lower() in global_references):
                continue
            problems.append(f"{where} ({framework}): {name} is restored at {dependency.get('version')!r}, "
                            "a version of its own; only the pin file chooses one")

    for key, library in sorted(assets.get("libraries", {}).items()):
        if library.get("type") != "package":
            continue
        name, _, version = key.partition("/")
        pin = pins.get(name.lower())
        if pin is not None and version.lower() != pin.lower():
            problems.append(f"{where}: {name} resolved to {version}, not the pin file's {pin}")
    return problems


def restored(assets: dict) -> list[str]:
    """The folder each package one restore resolved sits in, under every
    packages and fallback folder it read."""
    return [os.path.join(folder, library["path"])
            for library in assets.get("libraries", {}).values()
            if library.get("type") == "package" and library.get("path")
            for folder in assets.get("packageFolders", {})]


def judge(imported: list[tuple[str, str]], read_assets, *, root: str, dotnet: str, projects: list[str],
          config: str, sources: set[str], pinned: dict[tuple[str, str], str], tracked: set[str]) -> list[str]:
    """What is wrong with one build's imports and restores, the messages
    naming no position, so a commit and its fork point name one refusal
    alike. `read_assets` reads an assets file by path, None when absent."""
    if not imported:
        return ["the log names no import, so no build was judged: replay a binary log a build wrote"]
    problems: list[str] = []

    def shown(path: str) -> str:
        return os.path.relpath(path, root).replace(os.sep, "/") if inside(path, root) else path

    restores: dict[str, dict] = {}
    for directory in sorted({os.path.dirname(path) for path, _ in imported
                             if path.endswith(RESTORE_OUTPUT) and inside(path, root)}):
        path = os.path.join(directory, ASSETS)
        if _canonical(path) in tracked:
            problems.append(f"{shown(path)} is tracked, so the repository wrote what restore says it read")
            continue
        assets = read_assets(path)
        if assets is None:
            problems.append(f"{shown(directory)}: restore's output is imported, but no {ASSETS} sits beside it")
            continue
        restores[_canonical(assets.get("project", {}).get("restore", {}).get("projectPath", ""))] = assets

    importers = {_canonical(importer) for _, importer in imported}
    for project in projects:
        full = _canonical(os.path.join(root, project))
        if full not in importers:
            problems.append(f"{project}: the build never evaluated it, so nothing judged it")
        elif full not in restores:
            problems.append(f"{project}: the log holds no restore of it")
        else:
            problems += judge_assets(restores[full], where=project, root=root, dotnet=dotnet, config=config,
                                     sources=sources, pinned=pinned)

    packages = [folder for assets in restores.values() for folder in restored(assets)]
    for path, importer in sorted(set(imported)):
        if inside(path, root) or inside(path, dotnet) or any(inside(path, folder) for folder in packages):
            continue
        problems.append(f"{shown(importer)}: imports {path}, outside the repository, the .NET install "
                        "and the packages restore resolved")
    return list(dict.fromkeys(problems))


def replay(binlog: str, scratch: str) -> list[tuple[str, str]]:
    """Every import a binary log records, replayed through MSBuild's own
    diagnostic file logger, in English, which IMPORT reads."""
    if not os.path.isfile(binlog):
        raise JudgeError(f"{binlog} does not exist; the build writes it with -bl:<path>")
    out = os.path.join(scratch, "replay.log")
    done = subprocess.run(["dotnet", "msbuild", binlog, "-nologo", "-noconlog",
                           f"-flp:verbosity=diagnostic;logfile={out};encoding=utf-8"],
                          capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
                          env={**os.environ, "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                               "DOTNET_CLI_UI_LANGUAGE": "en"})
    if done.returncode != 0:
        raise JudgeError(f"replaying {binlog}: {(done.stdout + done.stderr).strip()[:2000]}")
    with open(out, encoding="utf-8-sig", errors="replace") as handle:
        return imports(handle)


def read_assets(path: str) -> dict | None:
    try:
        with open(path, encoding="utf-8-sig") as handle:
            return json.load(handle)
    except FileNotFoundError:
        return None
    except json.JSONDecodeError as error:
        raise JudgeError(f"{path} is not JSON: {error}") from error


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise JudgeError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__.splitlines()[0], file=sys.stderr)
        print("it takes the binary logs to judge: build with -bl:<path>, then name each", file=sys.stderr)
        return 2
    try:
        root = _git("rev-parse", "--show-toplevel").strip()
        paths = sorted(p for p in _git("ls-files", "-z").split("\0") if p)
        found = shutil.which("dotnet")
        if found is None:
            raise JudgeError("dotnet is not on PATH, so no log was replayed")
        dotnet = os.path.dirname(os.path.realpath(found))
        with open(os.path.join(root, PIN_FILE), "rb") as handle:
            pinned = pin_versions(handle.read())
        names = [name for name in os.listdir(root) if name.lower() == CONFIG]
        if len(names) != 1:
            raise JudgeError(f"the root holds {len(names)} files named {CONFIG} without regard to case, not one")
        config = os.path.join(root, names[0])
        with open(config, "rb") as handle:
            sources = config_sources(handle.read(), root)
        projects = [p for p in paths if PROJECT.search(p)]
        tracked = {_canonical(os.path.join(root, p)) for p in paths}
        problems: list[str] = []
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as scratch:
            for binlog in argv:
                problems += [f"{os.path.basename(binlog)}: {problem}" for problem in judge(
                    replay(binlog, scratch), read_assets, root=root, dotnet=dotnet, projects=projects,
                    config=config, sources=sources, pinned=pinned, tracked=tracked)]
    except (JudgeError, OSError) as error:
        print(f"::error::{error}")
        return 2
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"{len(argv)} build(s) judged across {len(projects)} projects; none imports from outside the "
          "repository, the .NET install and the packages it restored, or restores past the pin file")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
