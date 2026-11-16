"""The `npm start` child, killed from outside and stopped by the console; both claims are ProcessRunner.cs's. A
child that dies under the console reads `Exited`, non-zero, on `/api/stack`; and stop kills the whole tree, so nothing
still holds the client's port, where npm's own exit would leave its dev server serving. The dev server is found by
the port the `client` probe names, since the console publishes no pid."""

from __future__ import annotations

from urllib.parse import urlsplit

import harness

NAME = "frontend-killed"
CLAIM = "frontend stop leaves nothing on the client port, and a dev server killed from outside reads Exited, non-zero"
NEEDS_STACK = False
PROBE = "client"
SETTLE_SECONDS = 60
UP_SECONDS = 240
GONE_SECONDS = 60


def _port(world: harness.World) -> int:
    reach = world.console.reach(PROBE)
    if reach is None:
        raise harness.GameDayError(f"/api/stack has no {PROBE} probe to read the client's port from")
    parts = urlsplit(reach["url"])
    return parts.port or (443 if parts.scheme == "https" else 80)


def _job(world: harness.World) -> dict | None:
    return world.console.stack()["frontend"].get("job")


def settled(world: harness.World) -> tuple[bool, str]:
    frontend = world.console.stack()["frontend"]
    if not frontend.get("installed"):
        return False, f"the frontend clone is not installed, so npm start cannot run: {frontend.get('installProblem')}"
    job = frontend.get("job")
    held = world.ports.listeners(_port(world))
    running = job is not None and job.get("state") == "Running"
    return not running and not held, f"job={job and job.get('state')}, listeners on the client port={held}"


def _tail(world: harness.World, job_id: str, lines: int = 15) -> str:
    """The last lines `npm start` wrote, as the console holds them (`GET /api/jobs/{id}`), so a dev server that never
    listened says why: npm gone at once and one still compiling read alike on the port."""
    status, view = world.console.call("GET", f"/api/jobs/{job_id}")
    if status != 200 or not isinstance(view, dict):
        return f"(job {job_id}'s output unreadable: {status})"
    return " | ".join(line.get("text", "") for line in view.get("lines", [])[-lines:])


def _start(world: harness.World, port: int) -> None:
    status, body = world.console.call("POST", "/api/stack/frontend/start")
    if status != 202:
        raise harness.GameDayError(f"frontend start answered {status}: {str(body)[:300]}")
    job_id = body.get("id") if isinstance(body, dict) else None

    def listening() -> tuple[bool, str]:
        held = world.ports.listeners(port)
        job = _job(world)
        if not held and job is not None and job.get("state") == "Exited":
            raise harness.GameDayError(f"npm start exited {job.get('exitCode')} before listening on {port}: "
                                       f"{_tail(world, job['id']) if job.get('id') else ''}")
        return bool(held), f"listeners={held}, job={job and job.get('state')}"

    try:
        world.wait(listening, UP_SECONDS, "the dev server listening on the client port")
    except harness.Timeout as error:
        raise harness.Timeout(f"{error}; npm start's last lines: {_tail(world, job_id) if job_id else '(no job id)'}") \
            from error


def _exited(world: harness.World) -> tuple[bool, str]:
    job = _job(world)
    return job is not None and job.get("state") == "Exited", f"job={job}"


def cause(world: harness.World) -> None:
    """The second half of the check is the cause: kill what serves the port, from outside the console."""
    port = _port(world)
    world.notes["port"] = port
    _start(world, port)
    pids = world.ports.listeners(port)
    for pid in pids:
        world.ports.kill(pid)
    world.say(f"killed {pids}, the dev server on port {port}")


def check(world: harness.World) -> list[str]:
    findings = []
    try:
        world.wait(lambda: _exited(world), GONE_SECONDS, "the frontend job reading Exited after its dev server was killed")
        code = _job(world).get("exitCode")
        if not isinstance(code, int) or code == 0:
            findings.append(f"the killed frontend job reads Exited with exit code {code!r}, not a non-zero code")
        else:
            world.say(f"killed dev server: job Exited, exit code {code}")
    except harness.Timeout as error:
        findings.append(str(error))

    port = int(world.notes.get("port") or _port(world))
    job = _job(world)
    if job is not None and job.get("state") == "Running":
        # A second start would be refused while the console still holds the first; the finding above already says why.
        findings.append("the tree-kill half was not run: the killed job still reads Running")
        return findings
    try:
        _start(world, port)
    except harness.GameDayError as error:
        findings.append(f"the tree-kill half could not start the dev server again: {error}")
        return findings
    status, body = world.console.call("POST", "/api/stack/frontend/stop")
    if status != 200:
        findings.append(f"frontend stop answered {status}: {str(body)[:300]}")
    try:
        world.wait(lambda: (not world.ports.listeners(port), f"listeners={world.ports.listeners(port)}"),
                   GONE_SECONDS, "nothing on the client port after the console's stop")
        world.say("console stop: the tree is gone, nothing holds the client port")
    except harness.Timeout as error:
        findings.append(f"the console's stop left the dev server running, so the tree kill missed it: {error}")
    return findings


def restore(world: harness.World) -> None:
    job = _job(world)
    if job is not None and job.get("state") == "Running":
        world.console.call("POST", "/api/stack/frontend/stop")
    port = int(world.notes.get("port") or _port(world))
    for pid in world.ports.listeners(port):
        world.ports.kill(pid)
