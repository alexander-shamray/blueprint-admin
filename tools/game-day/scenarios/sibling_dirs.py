"""A wrong `Admin:BackendDir` or `Admin:FrontendDir`: the host must refuse to start, naming the key.

The claim is `RepoPaths.From` (src/Admin.Host/Config/RepoPaths.cs), resolved before `app.Run` in Program.cs so that a
wrong clone fails the host rather than its first command. A second host is started with one key pointed at a directory
that does not exist; the one under test is not touched, so there is nothing to restore."""

from __future__ import annotations

import harness

NAME = "sibling-dirs"
CLAIM = "a wrong Admin:BackendDir or Admin:FrontendDir stops the host at start, and the message names the key"
NEEDS_STACK = False
SETTLE_SECONDS = 0
KEYS = ("BackendDir", "FrontendDir")
# A port nothing else uses, so a host that wrongly starts cannot collide with the one under test.
SPARE_PORT = 5399
START_SECONDS = 180


def nowhere() -> str:
    return str(harness.REPO / "artifacts" / "game-day" / "no-such-clone")


def settled(world: harness.World) -> tuple[bool, str]:
    return True, "nothing to settle: the host under test is not touched"


def cause(world: harness.World) -> None:
    """Each check starts its own host; the cause is in the arguments."""


def check(world: harness.World) -> list[str]:
    findings = []
    for key in KEYS:
        args = [f"--Admin:{key}={nowhere()}", f"--Admin:Port={SPARE_PORT}", "--Admin:FakePlatform=false"]
        run = world.hosts.start_expecting_refusal(args, START_SECONDS)
        tail = run.output.strip()[-400:]
        if run.exit_code is None:
            findings.append(f"Admin:{key} wrong: the host was still running after {START_SECONDS}s; it should have refused")
        elif run.exit_code == 0:
            findings.append(f"Admin:{key} wrong: the host exited 0; a refusal must fail")
        elif f"Admin:{key}" not in run.output:
            findings.append(f"Admin:{key} wrong: the host exited {run.exit_code} without naming the key: {tail}")
        else:
            world.say(f"Admin:{key} wrong: refused, exit {run.exit_code}, naming the key")
    return findings


def restore(world: harness.World) -> None:
    """Nothing was changed."""
