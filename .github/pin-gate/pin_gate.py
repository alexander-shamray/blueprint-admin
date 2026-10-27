#!/usr/bin/env python3
"""No tracked MSBuild file chooses a package version, a feed or a pin file of its own.
The rules are .github/pin-gate/README.md's; run it as
py -3.12 .github/pin-gate/pin_gate.py from the repository root.
"""

from __future__ import annotations

import codecs
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

# The one solution, class E and denied; another is refused, so a project
# enters the build only by this file or by a ProjectReference.
SOLUTION_FILE = "BlueprintAdmin.slnx"

# What is read as MSBuild by its name: every *proj, .props and .targets at any
# depth, and the .user file the SDK imports beside a project. A file reaches
# evaluation under another name only through an import (refused below) or a
# project entry, and a project entry must name a *proj, so this list is the
# guarantee. Every other tracked file whose root is <Project> is read too, as
# a backstop that does not have to be complete.
MSBUILD_FILE = re.compile(r"\.(?:[a-z]*proj(?:\.user)?|props|targets)$", re.IGNORECASE)
PROJECT_NAME = re.compile(r"\.[a-z]*proj$", re.IGNORECASE)
PROJECT_ROOT = re.compile(
    r"^\s*(?:<\?[^>]*\?>\s*|<!--.*?-->\s*|<!DOCTYPE[^>]*>\s*)*<Project[\s>/]",
    re.IGNORECASE | re.DOTALL,
)

# Properties that turn central management off or redirect it, name a feed or
# a restore input, reach NuGet's own settings, import a file by path, or pick
# a runtime pack's version. A prefix, not a list of names: a property NuGet or
# the SDK adds next year under one of these is refused before anyone has read
# what it does. MSBuild reads every name in a project without regard to case,
# so the gate does too.
REFUSED_PROPERTY = re.compile(
    r"^(?:ManagePackageVersionsCentrally|CentralPackage|Restore|NuGet"
    r"|DirectoryPackagesProps|ImportDirectoryPackagesProps"
    r"|CustomBefore|CustomAfter|DirectoryBuild|ImportDirectoryBuild|ImportProjectExtension"
    r"|MSBuildProjectExtensionsPath|ForceImport|ImportByWildcard"
    r"|RuntimeFrameworkVersion)",
    re.IGNORECASE,
)

# Items that carry a version themselves: a second pin file's entries, a
# package every project takes, a download restore fetches by version, and the
# SDK's Known* pack items, whose metadata names the pack version restore
# downloads.
REFUSED_ITEMS = frozenset({"packageversion", "globalpackagereference", "packagedownload"})
REFUSED_ITEM_PREFIX = "known"
VERSION_METADATA = frozenset({"version", "versionoverride"})

# Code in a project file: a target runs during restore as readily as during a
# build, and inside one a CreateProperty, an <Output PropertyName> or an
# <MSBuild Properties> sets what no static rule above can see. No project here
# carries one; one that needs it is a change to this gate.
REFUSED_ELEMENTS = frozenset({"import", "sdk", "target", "usingtask"})

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


def _project_entries(path: str, what: str, value: str) -> list[str]:
    """A project entry naming a file the gate would not read as MSBuild."""
    return [f"{path}: {what} {entry!r} names no *proj file, so the gate cannot be sure it read it"
            for entry in (e.strip() for e in value.split(";")) if entry and not PROJECT_NAME.search(entry)]


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
        if tag in REFUSED_ELEMENTS:
            problems.append(f"{path}: <{element.tag}> brings in MSBuild the pin gate never reads")
        elif tag == "propertygroup":
            for child in element:
                if pin and PIN_PROPERTY.match(_name(child)):
                    continue
                if REFUSED_PROPERTY.match(_name(child)):
                    problems.append(f"{path}: <{_name(child)}> is the pin file's, NuGet's or an import's setting")
        # An item definition gives every item of its type default metadata,
        # so a Version there reaches each PackageReference in the project.
        elif tag in ("itemgroup", "itemdefinitiongroup"):
            for item in element:
                name = _name(item)
                if pin and name in PIN_ITEMS:
                    continue
                if name in REFUSED_ITEMS or name.startswith(REFUSED_ITEM_PREFIX):
                    problems.append(f"{path}: <{name}> sets a version outside {PIN_FILE}")
                elif name == "projectreference":
                    problems += _project_entries(path, "ProjectReference", _attrs(item).get("include", ""))
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
    # A condition, a second definition, or a definition anywhere but a
    # property group directly under <Project> (a <Choose>, a target) would
    # make the value read here one MSBuild may never evaluate, so each setting
    # is set once, at the top, unconditionally.
    top = {id(group) for group in root if _name(group) == "propertygroup"}
    found: dict[str, str] = {}
    for group in root.iter():
        if _name(group) != "propertygroup":
            continue
        for child in group:
            name = _name(child)
            if name not in PIN_SETTINGS:
                continue
            if id(group) not in top:
                problems.append(f"{PIN_FILE}: <{name}> is set below the top of the file")
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
    if base.endswith((".sln", ".slnx")):
        if path != SOLUTION_FILE:
            return [f"{path}: a second solution beside {SOLUTION_FILE}"]
        root, problems = _parse(path, text)
        if root is None:
            return problems
        return [p for element in root.iter() if _name(element) == "project"
                for p in _project_entries(path, "Project", _attrs(element).get("path", ""))]
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
    return base.endswith((".rsp", ".sln", ".slnx")) or base in (FEED_FILE, SDK_FILE)


def read(path: str) -> str | None:
    """A file's text, decoded as MSBuild would by its byte-order mark; None
    for one that is not text."""
    with open(path, "rb") as handle:
        data = handle.read()
    encoding = "utf-16" if data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)) else "utf-8-sig"
    try:
        return data.decode(encoding)
    except UnicodeDecodeError:
        return None


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def tracked() -> list[str]:
    return sorted(p for p in _git("ls-files", "-z").split("\0") if p)


def judge(path: str, text: str | None) -> list[str] | None:
    """The problems in one tracked file, or None when it is no restore input."""
    if path == PIN_FILE:
        return check_pin_file(text or "") + check(path, text or "", pin=True)
    if _is_other(path):
        return check_other(path, text or "")
    if MSBUILD_FILE.search(path):
        if text is None:
            return [f"{path}: is not text MSBuild could read, so nothing in it was read"]
        return check(path, text)
    if text is not None and PROJECT_ROOT.match(text):
        return check(path, text)
    return None


def collect() -> list[str]:
    """Every tracked restore input, the pin file included, as repository paths."""
    return [p for p in tracked() if judge(p, read(p)) is not None]


def main(argv: list[str]) -> int:
    if argv:
        print(__doc__.splitlines()[0], file=sys.stderr)
        print("it takes no arguments: it reads the tree it is run in", file=sys.stderr)
        return 2
    try:
        paths = tracked()
    except RuntimeError as error:
        print(f"::error::{error}")
        return 2
    if PIN_FILE not in paths:
        print(f"::error::{PIN_FILE} is not tracked, so there is no pin file to hold anything to")
        return 1
    problems: list[str] = []
    read_count = 0
    for path in paths:
        verdict = judge(path, read(path))
        if verdict is not None:
            read_count += 1
            problems += verdict
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"{read_count} restore inputs read; none chooses a version, a feed or a pin file")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
