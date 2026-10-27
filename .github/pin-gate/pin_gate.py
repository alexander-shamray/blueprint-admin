#!/usr/bin/env python3
"""No tracked MSBuild file chooses a package version, a feed or a pin file of its own.
The rules are .github/pin-gate/README.md's; run it as
py -3.12 .github/pin-gate/pin_gate.py from the repository root.
"""

from __future__ import annotations

import re
import subprocess
import sys
import xml.etree.ElementTree as ET

PIN_FILE = "Directory.Packages.props"

# What MSBuild reads as a project or an import: every *proj, .props and
# .targets, at any depth. A name outside this pattern is not evaluated unless
# something imports it, and an import is refused below.
MSBUILD_FILE = re.compile(r"\.(?:[A-Za-z]+proj|props|targets)$", re.IGNORECASE)

# Properties that turn central management off or redirect it, name a feed or
# a restore input, or reach NuGet's own settings. A prefix, not a list of
# names: a property NuGet adds next year under one of these is refused before
# anyone has read what it does.
REFUSED_PROPERTY = re.compile(
    r"^(?:ManagePackageVersionsCentrally|CentralPackage|Restore|NuGet"
    r"|DirectoryPackagesProps|ImportDirectoryPackagesProps)"
)

# Items that carry a version themselves: a second pin file's entries, a
# package every project takes, and a download restore fetches by version.
REFUSED_ITEMS = frozenset({"PackageVersion", "GlobalPackageReference", "PackageDownload"})

# An MSBuild SDK outside the ones the .NET SDK bundles is a NuGet package
# with a version of its own (`Name/1.2.3`, or `global.json`'s msbuild-sdks).
BUNDLED_SDK = re.compile(r"^Microsoft\.NET\.Sdk(?:\.[A-Za-z]+)*$")

# The pin file's own settings that this gate depends on, and their values.
PIN_SETTINGS = {
    "ManagePackageVersionsCentrally": "true",
    "CentralPackageVersionOverrideEnabled": "false",
}


def _local(tag: str) -> str:
    """A tag without the MSBuild 2003 namespace an old-style project carries."""
    return tag.rsplit("}", 1)[-1]


def _parse(path: str, text: str) -> tuple[ET.Element | None, list[str]]:
    # MSBuild has no use for a DTD, and refusing one leaves the stdlib parser
    # no entity to expand: the gate stays a stdlib script with no pip step.
    if "<!DOCTYPE" in text.upper():
        return None, [f"{path}: carries a DOCTYPE, which no MSBuild file needs, so nothing in it was read"]
    try:
        return ET.fromstring(text), []
    except ET.ParseError as error:
        return None, [f"{path}: is not well-formed XML ({error}), so nothing in it was read"]


def check(path: str, text: str) -> list[str]:
    """Every problem in one MSBuild file other than the pin file; empty is a pass."""
    root, problems = _parse(path, text)
    if root is None:
        return problems
    for name in filter(None, (s.strip() for s in root.get("Sdk", "").split(";"))):
        if not BUNDLED_SDK.match(name):
            problems.append(f"{path}: <Project Sdk=\"{name}\"> is not an SDK the .NET SDK bundles")
    for element in root.iter():
        tag = _local(element.tag) if isinstance(element.tag, str) else ""
        if tag in ("Import", "Sdk"):
            problems.append(f"{path}: <{tag}> brings in MSBuild the pin file never sees")
        elif tag == "PropertyGroup":
            for child in element:
                name = _local(child.tag) if isinstance(child.tag, str) else ""
                if REFUSED_PROPERTY.match(name):
                    problems.append(f"{path}: <{name}> is the pin file's or NuGet's setting")
        # An item definition gives every item of its type default metadata,
        # so a Version there reaches each PackageReference in the project.
        elif tag in ("ItemGroup", "ItemDefinitionGroup"):
            for item in element:
                name = _local(item.tag) if isinstance(item.tag, str) else ""
                if name in REFUSED_ITEMS:
                    problems.append(f"{path}: <{name}> sets a version outside {PIN_FILE}")
                elif name == "PackageReference":
                    chosen = [a for a in ("Version", "VersionOverride") if a in item.attrib]
                    chosen += [_local(c.tag) for c in item if isinstance(c.tag, str)
                               and _local(c.tag) in ("Version", "VersionOverride")]
                    for what in chosen:
                        package = item.get("Include") or item.get("Update") or "?"
                        problems.append(
                            f"{path}: PackageReference {package} sets {what}; its version is {PIN_FILE}'s"
                        )
    return problems


def check_pin_file(text: str) -> list[str]:
    """The pin file keeps central management on and project overrides off."""
    root, problems = _parse(PIN_FILE, text)
    if root is None:
        return problems
    found: dict[str, str] = {}
    for group in root.iter():
        if isinstance(group.tag, str) and _local(group.tag) == "PropertyGroup":
            for child in group:
                if isinstance(child.tag, str):
                    found[_local(child.tag)] = (child.text or "").strip().lower()
    for name, wanted in PIN_SETTINGS.items():
        if found.get(name) != wanted:
            problems.append(f"{PIN_FILE}: <{name}> must be {wanted}, and reads {found.get(name)!r}")
    return problems


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def collect() -> list[str]:
    """Every tracked MSBuild file, the pin file included, as repository paths."""
    return sorted(p for p in _git("ls-files", "-z").split("\0") if p and MSBUILD_FILE.search(p))


def main(argv: list[str]) -> int:
    if argv:
        print(__doc__.splitlines()[0], file=sys.stderr)
        print("it takes no arguments: it reads the tree it is run in", file=sys.stderr)
        return 2
    try:
        paths = collect()
    except RuntimeError as error:
        print(f"::error::{error}")
        return 2
    if PIN_FILE not in paths:
        print(f"::error::{PIN_FILE} is not tracked, so there is no pin file to hold anything to")
        return 1
    problems: list[str] = []
    for path in paths:
        with open(path, encoding="utf-8-sig") as handle:
            text = handle.read()
        problems += check_pin_file(text) if path == PIN_FILE else check(path, text)
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"{len(paths)} MSBuild files read; none chooses a version, a feed or a pin file")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
