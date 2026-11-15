"""The two Compose-service scenarios share one target and one settle: a service the Stack screen lists and probes."""

from __future__ import annotations

import harness

# `catalog` in PlatformServices.All (src/Admin.Host/Config/PlatformServices.cs): a Compose service with a probed URL,
# whose loss leaves the console and the stack's observability standing.
SERVICE = "catalog-api"
PROBE = "catalog"
SETTLE_SECONDS = 240
SEEN_SECONDS = 60


def settled(world: harness.World) -> tuple[bool, str]:
    row = world.console.service(SERVICE)
    reach = world.console.reach(PROBE)
    if row is None or reach is None:
        return False, f"/api/stack lists no {SERVICE} row or no {PROBE} probe"
    ok = row.get("state") == "running" and row.get("health") in (None, "healthy") and reach.get("up") is True
    return ok, f"{SERVICE} {row.get('state')}/{row.get('health')}, {PROBE} up={reach.get('up')}"


def shows(world: harness.World, state: str) -> list[str]:
    """The Stack screen's row reads the state Compose holds, and the probe reads the service down."""
    def seen() -> tuple[bool, str]:
        row = world.console.service(SERVICE)
        reach = world.console.reach(PROBE)
        if row is None or reach is None:
            return False, f"/api/stack lists no {SERVICE} row or no {PROBE} probe"
        return (row.get("state") == state and reach.get("up") is False,
                f"{SERVICE} state={row.get('state')}, {PROBE} up={reach.get('up')} status={reach.get('status')}")

    try:
        took = world.wait(seen, SEEN_SECONDS, f"/api/stack showing {SERVICE} {state} and {PROBE} down")
    except harness.Timeout as error:
        return [str(error)]
    world.say(f"/api/stack showed {SERVICE} {state} and {PROBE} down after {took:.0f}s")
    return []
