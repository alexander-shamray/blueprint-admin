"""Grafana, Loki and Tempo unreachable: one `grafana/otel-lgtm` container in the backend's infrastructure.yml, and
the host reaches Loki and Tempo only through Grafana's datasource proxy, so stopping it is the one real way to lose
them. The claim is GrafanaClient's: every failure is `reachable: false` with an error, never a throw, on the golden
signals, the alert strip and the event trace alike."""

from __future__ import annotations

import harness

NAME = "grafana-down"
CLAIM = "with Grafana (and so Loki and Tempo) stopped, health, alerts and trace read reachable=false with an error"
NEEDS_STACK = True
SERVICE = "grafana"
SETTLE_SECONDS = 300
SEEN_SECONDS = 60
# Any adoptable correlation id: the trace reads Loki first, and Loki is the part that is gone.
TRACE = "/api/trace/game-day-probe"
READS = ("/api/telemetry/health", "/api/telemetry/alerts", TRACE)


def settled(world: harness.World) -> tuple[bool, str]:
    health = world.console.get("/api/telemetry/health")
    reach = world.console.reach(SERVICE)
    ok = health.get("reachable") is True and reach is not None and reach.get("up") is True
    return ok, f"telemetry reachable={health.get('reachable')} error={health.get('error')}, probe up={reach and reach.get('up')}"


def cause(world: harness.World) -> None:
    world.compose.stop(SERVICE)


def check(world: harness.World) -> list[str]:
    findings = []
    for path in READS:
        def unreachable(path: str = path) -> tuple[bool, str]:
            view = world.console.get(path)
            return (view.get("reachable") is False and bool(view.get("error")),
                    f"reachable={view.get('reachable')} error={view.get('error')!r}")
        try:
            world.wait(unreachable, SEEN_SECONDS, f"{path} reading Grafana unreachable")
            world.say(f"{path}: reachable=false, with an error")
        except harness.Timeout as error:
            findings.append(str(error))
    reach = world.console.reach(SERVICE)
    if reach is None or reach.get("up") is not False:
        findings.append(f"/api/stack's {SERVICE} probe does not read down: {reach}")
    return findings


def restore(world: harness.World) -> None:
    world.compose.start(SERVICE)
