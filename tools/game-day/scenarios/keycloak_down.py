"""Keycloak unreachable.

The claim is `POST /api/identity/token` (IdentityEndpoints.cs): Keycloak not answering is a 502 titled
`Keycloak did not answer`, distinct from Keycloak's own refusal, which passes through as it was sent. The request
names a user and password made for this run, so TokenService's cache cannot answer in Keycloak's place."""

from __future__ import annotations

import secrets

import harness

NAME = "keycloak-down"
CLAIM = "with Keycloak stopped, a token request is a 502 titled `Keycloak did not answer`, and its probe reads down"
NEEDS_STACK = True
SERVICE = "keycloak"
SETTLE_SECONDS = 300
TITLE = "Keycloak did not answer"


def _identity() -> dict[str, str]:
    # Not a credential: a name and password no realm holds, so Keycloak, when up, refuses it.
    return {"username": f"game-day-{secrets.token_hex(4)}", "password": secrets.token_hex(12)}


def settled(world: harness.World) -> tuple[bool, str]:
    reach = world.console.reach(SERVICE)
    return reach is not None and reach.get("up") is True, f"{SERVICE} probe: {reach}"


def cause(world: harness.World) -> None:
    world.compose.stop(SERVICE)


def check(world: harness.World) -> list[str]:
    findings = []
    status, body = world.console.call("POST", "/api/identity/token", _identity())
    title = body.get("title") if isinstance(body, dict) else None
    if status != 502 or title != TITLE:
        findings.append(f"a token request with Keycloak stopped answered {status} {title!r}, not 502 {TITLE!r}: "
                        f"{str(body)[:300]}")
    else:
        world.say(f"token request: 502 {TITLE!r}")
    reach = world.console.reach(SERVICE)
    if reach is None or reach.get("up") is not False:
        findings.append(f"/api/stack's {SERVICE} probe does not read down: {reach}")
    return findings


def restore(world: harness.World) -> None:
    world.compose.start(SERVICE)
