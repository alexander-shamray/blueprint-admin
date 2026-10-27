#!/usr/bin/env python3
"""No tracked MSBuild file chooses a package version, a feed or a pin file of its own.
The rules are .github/pin-gate/README.md's; run it as
py -3.12 .github/pin-gate/pin_gate.py from the repository root.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import PurePosixPath

PIN_FILE = "Directory.Packages.props"

# The one feed list and the one SDK pin; both are class E and denied to every
# command that edits unattended. Any other copy of either is refused.
FEED_FILE = "nuget.config"
SDK_FILE = "global.json"

# What MSBuild reads as a project or an import: every *proj, .props and
# .targets, at any depth, and the .user file the SDK imports beside a project.
# A name outside this pattern is not evaluated unless something imports it,
# and an import is refused below.
MSBUILD_FILE = re.compile(r"\.(?:[a-z]+proj(?:\.user)?|props|targets)$", re.IGNORECASE)

# Properties that turn central management off or redirect it, name a feed or
# a restore input, or reach NuGet's own settings. A prefix, not a list of
# names: a property NuGet adds next year under one of these is refused before
# anyone has read what it does. MSBuild reads every name in a project
# without regard to case, so the gate does too.
REFUSED_PROPERTY = re.compile(
    r"^(?:ManagePackageVersionsCentrally|CentralPackage|Restore|NuGet"
    r"|DirectoryPackagesProps|ImportDirectoryPackagesProps)",
    re.IGNORECASE,
)

# Items that carry a version themselves: a second pin file's entries, a
# package every project takes, and a download restore fetches by version.
REFUSED_ITEMS = frozenset({"packageversion", "globalpackagereference", "packagedownload"})
VERSION_METADATA = frozenset({"version", "versionoverride"})

# What the pin file exists to hold, and so the one file allowed it. Its feed,
# restore and NuGet properties, imports and SDKs are refused as anywhere else.
PIN_ITEMS = frozenset({"packageversion", "globalpackagereference"})
PIN_PROPERTY = re.compile(r"^(?:ManagePackageVersionsCentrally|CentralPackage)", re.IGNORECASE)

# An MSBuild SDK outside the ones the .NET SDK bundles is a NuGet package
# with a version of its own (`Name/1.2.3`, or `global.json`'s msbuild-sdks),
# and some of those share the Microsoft.NET.Sdk prefix (`.Functions`), so the
# bundled ones are named. Another is a change to this list, which is denied.
BUNDLED_SDKS = frozenset(
    "microsoft.net.sdk" + suffix
    for suffix in ("", ".web", ".razor", ".worker", ".blazorwebassembly", ".windowsdesktop")
)

# The pin file's own settings that this gate depends on, and their values.
PIN_SETTINGS = {
    "managepackageversionscentrally": "true",
    "centralpackageversionoverrideenabled": "false",
}


def _name(node: ET.Element | str) -> str:
    """A tag or attribute name, lower-cased and without the MSBuild 2003
    namespace an old-style project carries; empty for a comment."""
    tag = node if isinstance(node, str) else node.tag
    return tag.rsplit("}", 1)[-1].lower() if isinstance(tag, str) else ""


def _attrs(element: ET.Element) -> dict[str, str]:
    return {_name(key): value for key, value in element.attrib.items()}


def _parse(path: str, text: str) -> tuple[ET.Element | None, list[str]]:
    # MSBuild has no use for a DTD, and refusing one leaves the stdlib parser
    # no entity to expand: the gate stays a stdlib script with no pip step.
    if "<!DOCTYPE" in text.upper():
        return None, [f"{path}: carries a DOCTYPE, which no MSBuild file needs, so nothing in it was read"]
    try:
        return ET.fromstring(text), []
    except ET.ParseError as error:
        return None, [f"{path}: is not well-formed XML ({error}), so nothing in it was read"]


def check(path: str, text: str, *, pin: bool = False) -> list[str]:
    """Every problem in one MSBuild file; empty is a pass. The pin file is
    read the same way, save for the items and settings it exists to hold."""
    root, problems = _parse(path, text)
    if root is None:
        return problems
    for name in filter(None, (s.strip() for s in _attrs(root).get("sdk", "").split(";"))):
        if name.lower() not in BUNDLED_SDKS:
            problems.append(f"{path}: <Project Sdk=\"{name}\"> is not an SDK the .NET SDK bundles")
    for element in root.iter():
        tag = _name(element)
        if tag in ("import", "sdk"):
            problems.append(f"{path}: <{element.tag}> brings in MSBuild the pin file never sees")
        elif tag == "propertygroup":
            for child in element:
                if pin and PIN_PROPERTY.match(_name(child)):
                    continue
                if REFUSED_PROPERTY.match(_name(child)):
                    problems.append(f"{path}: <{_name(child)}> is the pin file's or NuGet's setting")
        # An item definition gives every item of its type default metadata,
        # so a Version there reaches each PackageReference in the project.
        elif tag in ("itemgroup", "itemdefinitiongroup"):
            for item in element:
                name = _name(item)
                if pin and name in PIN_ITEMS:
                    continue
                if name in REFUSED_ITEMS:
                    problems.append(f"{path}: <{name}> sets a version outside {PIN_FILE}")
                elif name == "packagereference":
                    attrs = _attrs(item)
                    chosen = sorted(VERSION_METADATA & attrs.keys())
                    chosen += [_name(c) for c in item if _name(c) in VERSION_METADATA]
                    package = attrs.get("include") or attrs.get("update") or "?"
                    for what in chosen:
                        problems.append(
                            f"{path}: PackageReference {package} sets {what}; its version is {PIN_FILE}'s"
                        )
    return problems


def check_pin_file(text: str) -> list[str]:
    """The pin file keeps central management on and project overrides off."""
    root, problems = _parse(PIN_FILE, text)
    if root is None:
        return problems
    # A condition or a second definition would make the value read here one
    # MSBuild may never evaluate, so each setting is set once, unconditionally.
    found: dict[str, str] = {}
    for group in root.iter():
        if _name(group) == "propertygroup":
            for child in group:
                name = _name(child)
                if name not in PIN_SETTINGS:
                    continue
                if "condition" in _attrs(child) or "condition" in _attrs(group):
                    problems.append(f"{PIN_FILE}: <{name}> is set under a Condition")
                if name in found:
                    problems.append(f"{PIN_FILE}: <{name}> is set more than once")
                found[name] = (child.text or "").strip().lower()
    for name, wanted in PIN_SETTINGS.items():
        if found.get(name) != wanted:
            problems.append(f"{PIN_FILE}: <{name}> must be {wanted}, and reads {found.get(name)!r}")
    return problems


def check_other(path: str, text: str) -> list[str]:
    """A tracked restore input that is not an MSBuild file: a response file,
    a second feed list, or an SDK pin naming MSBuild SDKs to fetch."""
    base = PurePosixPath(path).name.lower()
    if base.endswith(".rsp"):
        return [f"{path}: a response file hands MSBuild properties no project shows"]
    if base == FEED_FILE and path != FEED_FILE:
        return [f"{path}: a second feed list beside the root {FEED_FILE}"]
    if base == SDK_FILE:
        try:
            pins = json.loads(text)
        except ValueError as error:
            return [f"{path}: is not JSON ({error}), so nothing in it was read"]
        if isinstance(pins, dict) and any(k.lower() == "msbuild-sdks" for k in pins):
            return [f"{path}: msbuild-sdks fetches MSBuild SDKs by a version {PIN_FILE} never sees"]
    return []


def _is_other(path: str) -> bool:
    base = PurePosixPath(path).name.lower()
    return base.endswith(".rsp") or base in (FEED_FILE, SDK_FILE)


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def collect() -> list[str]:
    """Every tracked restore input, the pin file included, as repository paths."""
    return sorted(p for p in _git("ls-files", "-z").split("\0")
                  if p and (MSBUILD_FILE.search(p) or _is_other(p)))


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
        if path == PIN_FILE:
            problems += check_pin_file(text) + check(path, text, pin=True)
        elif _is_other(path):
            problems += check_other(path, text)
        else:
            problems += check(path, text)
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"{len(paths)} restore inputs read; none chooses a version, a feed or a pin file")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
