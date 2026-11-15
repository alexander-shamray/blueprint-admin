"""Cause, for real, a failure the console claims to report, and check the console reports it.
Usage: py -3.12 tools/game-day/game_day.py service-stopped (or all, or --list); add --cause to cause and leave, and
--restore to put it back. The host must be running against the real platform; README.md owns the rest.
"""

from __future__ import annotations

import argparse
import importlib
import shlex
import sys
from pathlib import Path
from types import ModuleType

sys.path.insert(0, str(Path(__file__).resolve().parent))

import harness  # noqa: E402

# The order a full run takes: the scenarios that touch no service first, then the stack, the broker last, since
# every backend service reconnects to it after its restore.
SCENARIOS = ("sibling_dirs", "frontend_killed", "service_stopped", "service_paused", "keycloak_down", "grafana_down",
             "broker_down")
REQUIRED = ("NAME", "CLAIM", "NEEDS_STACK", "SETTLE_SECONDS", "settled", "cause", "check", "restore")


def load_all() -> list[ModuleType]:
    return [importlib.import_module(f"scenarios.{module}") for module in SCENARIOS]


def by_name(name: str) -> ModuleType:
    for scenario in load_all():
        if scenario.NAME == name:
            return scenario
    raise harness.GameDayError(f"{name}: no such scenario; --list names them")


def quiet_start(scenario: ModuleType, world: harness.World) -> str | None:
    """Why the cause must not start, or None. A check that passes is only evidence when the failure was not there
    before the cause; a stack already broken would make every scenario after it a finding about the wrong thing."""
    try:
        ok, seen = scenario.settled(world)
    except harness.GameDayError as error:
        return f"{scenario.NAME}: could not read the starting state: {error}"
    return None if ok else f"{scenario.NAME}: not starting from a settled state ({seen}); restore it and rerun"


def run(scenario: ModuleType, world: harness.World, *, cause: bool = True, restore: bool = True) -> list[str]:
    """One scenario. Returns its findings; an empty list is a pass.

    The restore runs whatever the cause did, including when the cause or the check raised half-way, because a
    scenario that left keycloak stopped would turn every one after it into a finding about the wrong thing."""
    findings: list[str] = []
    if cause:
        refused = quiet_start(scenario, world)
        if refused:
            return [refused]
    try:
        if cause:
            world.say(f"{scenario.NAME}: causing it; the claim: {scenario.CLAIM}")
            scenario.cause(world)
            findings.extend(scenario.check(world))
    except harness.GameDayError as error:
        findings.append(f"the cause or the check failed: {error}")
    finally:
        if restore:
            findings.extend(_restore(scenario, world))
    return [f"{scenario.NAME}: {finding}" if not finding.startswith(scenario.NAME) else finding for finding in findings]


def _restore(scenario: ModuleType, world: harness.World) -> list[str]:
    try:
        scenario.restore(world)
        if scenario.SETTLE_SECONDS:
            world.wait(lambda: scenario.settled(world), scenario.SETTLE_SECONDS, f"{scenario.NAME} settling after the restore")
    except Exception as error:  # noqa: BLE001 - a restore that fails for any reason is the finding
        return [f"the restore did not settle, so the next scenario starts poisoned: {error}"]
    world.say(f"{scenario.NAME}: restored")
    return []


def build_world(args: argparse.Namespace) -> harness.World:
    return harness.World(
        console=harness.Console(args.host),
        compose=harness.Compose(Path(args.compose_file)),
        ports=harness.Ports(),
        hosts=harness.Hosts(shlex.split(args.host_command)))


def parse(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("scenario", nargs="?", help="a scenario's name, or all")
    parser.add_argument("--list", action="store_true", help="name each scenario and its claim, and exit")
    halves = parser.add_mutually_exclusive_group()
    halves.add_argument("--cause", action="store_true", help="cause and check, and leave the failure in place")
    halves.add_argument("--restore", action="store_true", help="only put back what --cause left")
    parser.add_argument("--host", default="http://127.0.0.1:5300", help="the console under test (Admin:Port's default)")
    parser.add_argument("--compose-file", default=str(harness.REPO.parent / "blueprint-backend" / "deploy" / "compose"
                                                      / "docker-compose.yml"),
                        help="the backend's Compose file, as Admin:BackendDir and Admin:ComposeFile resolve it")
    parser.add_argument("--host-command", default="dotnet run --project src/Admin.Host --no-build --no-launch-profile --",
                        help="how to start a second host, from the repository root, for sibling-dirs")
    args = parser.parse_args(argv)
    if not args.list and not args.scenario:
        parser.error("name a scenario, all, or --list")
    return args


def main(argv: list[str] | None = None, world: harness.World | None = None) -> int:
    args = parse(sys.argv[1:] if argv is None else argv)
    if args.list:
        for scenario in load_all():
            print(f"{scenario.NAME}: {scenario.CLAIM}")
        return 0
    try:
        chosen = load_all() if args.scenario == "all" else [by_name(args.scenario)]
    except harness.GameDayError as error:
        print(error, file=sys.stderr)
        return 2
    world = world or build_world(args)
    findings: list[str] = []
    for scenario in chosen:
        findings.extend(run(scenario, world, cause=not args.restore, restore=not args.cause))
    for finding in findings:
        print(f"FINDING: {finding}")
    print(f"{len(chosen)} scenario(s), {len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
