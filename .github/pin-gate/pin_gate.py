#!/usr/bin/env python3
"""No tracked MSBuild file chooses a package version, a feed or a pin file of its own.
The rules are .github/pin-gate/README.md's; run it as
py -3.12 .github/pin-gate/pin_gate.py from the repository root.
"""

from __future__ import annotations

import json
import posixpath
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import PurePosixPath
from xml.parsers import expat

PIN_FILE = "Directory.Packages.props"

# The one feed list, the one SDK pin and the one solution; each is class E
# and denied to every command that edits unattended. Any other copy is
# refused, so a project enters the build only by the solution or by a
# ProjectReference.
FEED_FILE = "nuget.config"
SDK_FILE = "global.json"
SOLUTION_FILE = "BlueprintAdmin.slnx"

# What is read as MSBuild by its name: every *proj, .props and .targets at any
# depth, and the .user file the SDK imports beside a project. A file whose
# root is <Project> may be imported under any other name too, so every
# tracked file is parsed, and each with that root is read whatever it is
# called; MSBuild imports nothing else.
MSBUILD_FILE = re.compile(r"\.(?:[a-z]*proj(?:\.user)?|props|targets)$", re.IGNORECASE)

# A project entry is a literal path to a *proj: anything MSBuild expands
# ($(...), @(...), %(...), a wildcard) can become several paths, or another
# name, after the gate has read it.
PROJECT_ENTRY = re.compile(r"^[^$@%*?]*\.[a-z]*proj$", re.IGNORECASE)
ROOTED = re.compile(r"^(?:[A-Za-z]:|[\\/])")

# Properties that turn central management off or redirect it, name a feed or
# a restore input, reach NuGet's own settings, import a file by path, or pick
# a runtime pack's version. A prefix, not a list of names: a property NuGet or
# the SDK adds next year under one of these is refused before anyone has read
# what it does. MSBuild reads every name in a project without regard to case,
# so the gate does too. The import-path names are defence in depth: whatever a
# property imports must have a <Project> root, and so is read anyway.
REFUSED_PROPERTY = re.compile(
    r"^(?:ManagePackageVersionsCentrally|CentralPackage|Restore|NuGet"
    r"|DirectoryPackagesProps|ImportDirectoryPackagesProps"
    r"|CustomBefore|CustomAfter|DirectoryBuild|ImportDirectoryBuild|ImportProjectExtension"
    r"|MSBuildProjectExtensionsPath|MSBuildExtensionsPath|MSBuildUserExtensionsPath"
    r"|ForceImport|ImportByWildcard|ImportUserLocations|LanguageTargets"
    r"|BeforeMicrosoftNET|AfterMicrosoftNET"
    r"|RuntimeFrameworkVersion)"
    # And by suffix: the SDK names the version of each pack it downloads
    # <Something>PackageVersion (WindowsSdkPackageVersion,
    # MicrosoftNETCoreAppRefPackageVersion and their kin).
    r"|^.*PackageVersion$",
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


ENCODING_ERRORS = frozenset(
    expat.errors.codes[message]
    for message in (expat.errors.XML_ERROR_UNKNOWN_ENCODING, expat.errors.XML_ERROR_INCORRECT_ENCODING)
)


class _Doctype(Exception):
    pass


def _refuse_doctype(*_: object) -> None:
    raise _Doctype


def _name(node: ET.Element | str) -> str:
    """A tag or attribute name, lower-cased and without its namespace; empty
    for a comment."""
    tag = node if isinstance(node, str) else node.tag
    return tag.rsplit("}", 1)[-1].lower() if isinstance(tag, str) else ""


def _attrs(element: ET.Element) -> dict[str, str]:
    return {_name(key): value for key, value in element.attrib.items()}


def _parse(path: str, data: bytes | str) -> tuple[ET.Element | None, list[str]]:
    """The document, read from its bytes as XmlReader reads them: expat honours
    the byte-order mark and the declared encoding, and tells UTF-16 from its
    first bytes. A DOCTYPE is refused as it opens, before any entity in it is
    read: MSBuild has no use for one, and the gate stays stdlib with no pip
    step."""
    probe = expat.ParserCreate()
    probe.StartDoctypeDeclHandler = _refuse_doctype
    try:
        probe.Parse(data, True)
    except _Doctype:
        return None, [f"{path}: carries a DOCTYPE, which no MSBuild file needs, so nothing in it was read"]
    except (expat.ExpatError, ValueError, LookupError) as error:
        return None, [f"{path}: is not well-formed XML ({error}), so nothing in it was read"]
    return ET.fromstring(data), []


def _project_entries(path: str, what: str, value: str, known: frozenset[str] | None) -> list[str]:
    """A project entry that is not a literal *proj path to a tracked file. A
    rooted, drive or UNC path, or one that climbs out of the repository,
    names MSBuild the gate never reads; a share would hand it over the
    network. `known` is every tracked path, lower-cased as Windows matches
    them; None, for a file judged alone, checks the entry's form only."""
    problems = []
    base = posixpath.dirname(path)
    for entry in (e.strip() for e in value.split(";")):
        if not entry:
            continue
        if not PROJECT_ENTRY.match(entry):
            problems.append(f"{path}: {what} {entry!r} is not a literal *proj path, "
                            "so the gate cannot be sure it read it")
        elif ROOTED.match(entry):
            problems.append(f"{path}: {what} {entry!r} is rooted outside the repository")
        elif known is not None:
            target = posixpath.normpath(posixpath.join(base, entry.replace("\\", "/")))
            if target.lower() not in known:
                problems.append(f"{path}: {what} {entry!r} names no tracked file in the repository")
    return problems


def check(path: str, data: bytes | str, *, pin: bool = False, known: frozenset[str] | None = None) -> list[str]:
    """Every problem in one MSBuild file; empty is a pass. The pin file is
    read the same way, save for the items and settings it exists to hold."""
    root, problems = _parse(path, data)
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
                    problems += _project_entries(path, "ProjectReference", _attrs(item).get("include", ""), known)
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


def check_pin_file(data: bytes | str) -> list[str]:
    """The pin file keeps central management on and project overrides off."""
    root, problems = _parse(PIN_FILE, data)
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


def check_other(path: str, data: bytes | str, known: frozenset[str] | None = None) -> list[str]:
    """A tracked restore input that is not an MSBuild file: a response file,
    a second solution or feed list, or an SDK pin naming MSBuild SDKs."""
    base = PurePosixPath(path).name.lower()
    if base.endswith(".rsp"):
        return [f"{path}: a response file hands MSBuild properties no project shows"]
    if base.endswith((".sln", ".slnx")):
        if path != SOLUTION_FILE:
            return [f"{path}: a second solution beside {SOLUTION_FILE}"]
        root, problems = _parse(path, data)
        if root is None:
            return problems
        return [p for element in root.iter() if _name(element) == "project"
                for p in _project_entries(path, "Project", _attrs(element).get("path", ""), known)]
    if base == FEED_FILE and path != FEED_FILE:
        return [f"{path}: a second feed list beside the root {FEED_FILE}"]
    if base == SDK_FILE:
        try:
            pins = json.loads(data)
        except ValueError as error:
            return [f"{path}: is not JSON ({error}), so nothing in it was read"]
        if isinstance(pins, dict) and any(k.lower() == "msbuild-sdks" for k in pins):
            return [f"{path}: msbuild-sdks fetches MSBuild SDKs by a version {PIN_FILE} never sees"]
    return []


def _is_other(path: str) -> bool:
    base = PurePosixPath(path).name.lower()
    return base.endswith((".rsp", ".sln", ".slnx")) or base in (FEED_FILE, SDK_FILE)


class _Root(Exception):
    def __init__(self, name: str) -> None:
        super().__init__(name)
        self.name = name


def _start(name: str, _attrs: object) -> None:
    raise _Root(name)


def _is_project(data: bytes) -> bool:
    """Whether MSBuild could import a file: its first element is <Project>, in
    any namespace or prefix. Parsing stops at that element, so nothing past
    it is read, and an entity declaration stops it sooner: a file whose root
    the gate cannot name without expanding one is read, and refused, rather
    than skipped. A file that is not XML up to its root is no import."""
    # UTF-32, by its byte-order mark or by `<` as XmlReader detects it without
    # one, is text MSBuild reads and expat does not: read it, and refuse it.
    if data[:4] in (b"\xff\xfe\x00\x00", b"\x00\x00\xfe\xff", b"<\x00\x00\x00", b"\x00\x00\x00<"):
        return True
    seen = {"doctype": False}
    probe = expat.ParserCreate()
    probe.StartDoctypeDeclHandler = lambda *_: seen.update(doctype=True)
    probe.EntityDeclHandler = _refuse_doctype
    probe.StartElementHandler = _start
    try:
        probe.Parse(data, True)
    except _Root as root:
        return _name(root.name.rsplit(":", 1)[-1]) == "project"
    except _Doctype:
        return True
    except expat.ExpatError as error:
        # An encoding expat does not know (Shift_JIS, say) is one XmlReader
        # may: read the file, and refuse it, rather than skip it. Any other
        # failure before the root is a file XmlReader rejects too, such as
        # an HTML page's lower-case doctype.
        return error.code in ENCODING_ERRORS
    except (ValueError, LookupError):
        # LookupError is pyexpat asking Python's codecs for a name they lack.
        return True
    return seen["doctype"]


def judge(path: str, data: bytes, known: frozenset[str]) -> list[str] | None:
    """The problems in one tracked file, or None when it is no restore input."""
    if path == PIN_FILE:
        return check_pin_file(data) + check(path, data, pin=True, known=known)
    if _is_other(path):
        return check_other(path, data, known)
    if MSBUILD_FILE.search(path) or _is_project(data):
        return check(path, data, known=known)
    return None


def _git(*args: str) -> str:
    done = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {done.stderr.strip()}")
    return done.stdout


def tracked() -> list[str]:
    return sorted(p for p in _git("ls-files", "-z").split("\0") if p)


def read(path: str) -> bytes:
    with open(path, "rb") as handle:
        return handle.read()


def collect() -> list[str]:
    """Every tracked restore input, the pin file included, as repository paths."""
    paths = tracked()
    known = frozenset(p.lower() for p in paths)
    return [p for p in paths if judge(p, read(p), known) is not None]


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
    known = frozenset(p.lower() for p in paths)
    problems: list[str] = []
    read_count = 0
    for path in paths:
        verdict = judge(path, read(path), known)
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
