"""A Compose service stopped while the dashboard is open.

The claim is `GET /api/stack` (StackEndpoints.ReadAsync): its backend rows are `docker compose ps -a` as
ComposePsParser reads it, so a stopped service reads `exited`, and PlatformProbe reads its URL down."""

from __future__ import annotations

import harness
from scenarios import _compose_row as row

NAME = "service-stopped"
CLAIM = f"a stopped {row.SERVICE} reads `exited` on /api/stack and its probe reads down"
NEEDS_STACK = True
SETTLE_SECONDS = row.SETTLE_SECONDS
settled = row.settled


def cause(world: harness.World) -> None:
    world.compose.stop(row.SERVICE)


def check(world: harness.World) -> list[str]:
    return row.shows(world, "exited")


def restore(world: harness.World) -> None:
    world.compose.start(row.SERVICE)
