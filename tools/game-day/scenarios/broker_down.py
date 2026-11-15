"""The broker unreachable while a publish's drain is watched. The watch is the SPA's (`PUBLISH_OPERATION` in
api-client.ts), polling `GET /api/broker/queues` for its `projection` row, so the host's half of the claim is that
read: with RabbitMQ stopped, BrokerService.QueuesAsync answers `reachable: false` with the error and a projection
not found, never a 500 or a drained queue."""

from __future__ import annotations

import harness

NAME = "broker-down"
CLAIM = "with RabbitMQ stopped, /api/broker/queues reads reachable=false with an error and the projection not found"
NEEDS_STACK = True
# BrokerService.Service.
SERVICE = "rabbitmq"
SETTLE_SECONDS = 300
SEEN_SECONDS = 60


def _queues(world: harness.World) -> dict:
    return world.console.get("/api/broker/queues")


def settled(world: harness.World) -> tuple[bool, str]:
    view = _queues(world)
    projection = view.get("projection") or {}
    ok = view.get("reachable") is True and projection.get("found") is True
    return ok, f"reachable={view.get('reachable')} projection={projection} error={view.get('error')!r}"


def cause(world: harness.World) -> None:
    world.compose.stop(SERVICE)


def check(world: harness.World) -> list[str]:
    def unreachable() -> tuple[bool, str]:
        view = _queues(world)
        projection = view.get("projection") or {}
        ok = (view.get("reachable") is False and bool(view.get("error")) and projection.get("found") is False
              and projection.get("drained") is not True)
        return ok, f"reachable={view.get('reachable')} projection={projection} error={view.get('error')!r}"

    try:
        world.wait(unreachable, SEEN_SECONDS, "/api/broker/queues reading the broker unreachable")
    except harness.Timeout as error:
        return [str(error)]
    world.say("/api/broker/queues: reachable=false, projection not found and not drained")
    return []


def restore(world: harness.World) -> None:
    world.compose.start(SERVICE)
