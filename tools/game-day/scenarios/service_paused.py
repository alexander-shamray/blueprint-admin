"""A Compose service paused while the dashboard is open.

A paused container holds its port and answers nothing, which is the case a refused connection does not cover: the
probe must time out (PlatformProbe's per-target bound) rather than hang the Stack screen, and the row must say
`paused` rather than `running`."""

from __future__ import annotations

import harness
from scenarios import _compose_row as row

NAME = "service-paused"
CLAIM = f"a paused {row.SERVICE} reads `paused` on /api/stack, and its probe times out to down"
NEEDS_STACK = True
SETTLE_SECONDS = row.SETTLE_SECONDS
settled = row.settled


def cause(world: harness.World) -> None:
    world.compose.pause(row.SERVICE)


def check(world: harness.World) -> list[str]:
    return row.shows(world, "paused")


def restore(world: harness.World) -> None:
    world.compose.unpause(row.SERVICE)
