"""What every scenario shares: the console's API, Compose, the processes on a port, a host started on purpose to fail,
and waits that are predicates with deadlines. Standard library only, so the stack-free suite needs no install."""

from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

REPO = Path(__file__).resolve().parents[2]


def main_checkout(root: Path) -> Path:
    """The checkout the siblings sit beside: `root` itself, or, for a linked worktree, the main checkout it belongs
    to, read as RepoRoot.MainCheckout (src/Admin.Host/Config/RepoRoot.cs) reads it, so a default here resolves where
    the host's Admin:BackendDir does."""
    dot_git = root / ".git"
    if not dot_git.is_file():
        return root
    pointer = dot_git.read_text(encoding="utf-8").strip()
    if not pointer.startswith("gitdir:"):
        return root
    git_dir = (root / pointer.removeprefix("gitdir:").strip()).resolve()
    common_file = git_dir / "commondir"
    if not common_file.is_file():
        return root
    common = (git_dir / common_file.read_text(encoding="utf-8").strip()).resolve()
    return common.parent if common.name == ".git" else root


class GameDayError(Exception):
    """A cause, a check's read or a restore that could not be done; the runner turns it into a finding."""


class Timeout(GameDayError):
    pass


def wait_until(predicate: Callable[[], tuple[bool, str]], seconds: float, what: str, *,
               clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep,
               every: float = 2.0) -> float:
    """Poll until the predicate holds; returns how long it took. The predicate says what it saw, so a timeout names
    the last state rather than only that the deadline passed."""
    started = clock()
    while True:
        ok, seen = predicate()
        if ok:
            return clock() - started
        if clock() - started >= seconds:
            raise Timeout(f"{what}: not within {seconds:.0f}s; last seen: {seen}")
        sleep(every)


Http = Callable[[str, str, object | None], tuple[int, object]]


def _decode(raw: bytes) -> object:
    text = raw.decode("utf-8", errors="replace")
    try:
        return json.loads(text) if text else None
    except json.JSONDecodeError:
        return text


def http(method: str, url: str, body: object | None = None) -> tuple[int, object]:
    """One request; a status and the decoded body, whatever the status. A refused connection is a GameDayError,
    because every read here is of the console, and a console that does not answer cannot report anything."""
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers = {"Content-Type": "application/json"} if data is not None else {}
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, _decode(response.read())
    except urllib.error.HTTPError as error:
        return error.code, _decode(error.read())
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        raise GameDayError(f"{method} {url}: the console did not answer: {error}") from error


class Console:
    """The host's `/api`, which is what every screen renders: a check reads the field the screen shows."""

    def __init__(self, base: str, send: Http = http) -> None:
        self.base = base.rstrip("/")
        self._send = send

    def call(self, method: str, path: str, body: object | None = None) -> tuple[int, object]:
        return self._send(method, self.base + path, body)

    def get(self, path: str) -> dict:
        status, body = self.call("GET", path)
        if status != 200 or not isinstance(body, dict):
            raise GameDayError(f"GET {path} answered {status}: {str(body)[:300]}")
        return body

    def stack(self) -> dict:
        return self.get("/api/stack")

    def service(self, name: str) -> dict | None:
        """The Compose row the Stack screen shows for one service, or None when `ps` lists no such service."""
        backend = self.stack()["backend"]
        return next((row for row in backend.get("services", []) if row.get("service") == name), None)

    def reach(self, name: str) -> dict | None:
        return next((row for row in self.stack()["reachability"] if row.get("name") == name), None)


def run_process(args: list[str], *, cwd: Path | None = None, timeout: float = 300,
                stdin: str | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(args, cwd=cwd, input=stdin, capture_output=True, text=True, timeout=timeout, check=False)


class Compose:
    """The backend's stack, driven as run-locally.md drives it: `docker compose -f <file>`."""

    def __init__(self, compose_file: Path, run: Callable[..., subprocess.CompletedProcess] = run_process) -> None:
        self.compose_file = compose_file
        self._run = run

    def _call(self, *args: str) -> str:
        done = self._run(["docker", "compose", "-f", str(self.compose_file), *args])
        if done.returncode != 0:
            raise GameDayError(f"docker compose {' '.join(args)} exited {done.returncode}: {done.stderr.strip()[:300]}")
        return done.stdout

    def stop(self, service: str) -> None:
        self._call("stop", service)

    def start(self, service: str) -> None:
        self._call("start", service)

    def pause(self, service: str) -> None:
        self._call("pause", service)

    def unpause(self, service: str) -> None:
        self._call("unpause", service)

    def state(self, service: str) -> str:
        """`running`, `exited`, `paused` and so on, from Compose itself rather than from the console under test."""
        out = self._call("ps", "-a", "--format", "json", service).strip()
        rows = [json.loads(line) for line in out.splitlines() if line.strip()] if not out.startswith("[") else json.loads(out)
        return rows[0].get("State", "") if rows else "absent"


class Ports:
    """The processes listening on a local port. The console never publishes a child's pid, so a child is found the
    way a developer would find it: by what holds the port it serves."""

    def __init__(self, run: Callable[..., subprocess.CompletedProcess] = run_process, platform: str = sys.platform) -> None:
        self._run = run
        self._platform = platform

    def listeners(self, port: int) -> list[int]:
        if self._platform.startswith("win"):
            done = self._run(["powershell", "-NoProfile", "-Command",
                              f"(Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue)"
                              ".OwningProcess"])
            return sorted({int(word) for word in done.stdout.split() if word.isdigit() and int(word) > 0})
        if self._platform.startswith("linux"):
            # ss, not lsof: on ubuntu-latest ss names the pid of a listener that `lsof -t` misses.
            done = self._run(["ss", "-Hltnp", f"sport = :{port}"])
            pids = {int(pid) for pid in re.findall(r"pid=(\d+)", done.stdout)}
        else:
            done = self._run(["lsof", "-t", f"-iTCP:{port}", "-sTCP:LISTEN"])
            pids = {int(word) for word in done.stdout.split() if word.isdigit()}
        if not pids and done.stderr.strip():
            raise GameDayError(f"could not read the listeners on {port}: {done.stderr.strip()[:200]}")
        return sorted(pid for pid in pids if pid > 0)

    def kill(self, pid: int) -> None:
        if self._platform.startswith("win"):
            done = self._run(["taskkill", "/F", "/PID", str(pid)])
        else:
            done = self._run(["kill", "-KILL", str(pid)])
        if done.returncode != 0:
            raise GameDayError(f"could not kill {pid}: {(done.stderr or done.stdout).strip()[:200]}")


@dataclass
class HostRun:
    exit_code: int | None
    output: str


class Hosts:
    """A second host, started only to watch it refuse: it is never the one under test, so it takes its own port and
    is killed if it does start."""

    def __init__(self, command: list[str], cwd: Path = REPO) -> None:
        self.command = command
        self.cwd = cwd

    def start_expecting_refusal(self, args: list[str], seconds: float) -> HostRun:
        # `dotnet run` starts the host as a child that holds the output pipe, so a host that does start is killed as a
        # tree, from a group of its own; killing the CLI alone would leave it serving and the read below never ending.
        group = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if sys.platform.startswith("win")
                 else {"start_new_session": True})
        with subprocess.Popen([*self.command, *args], cwd=self.cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, env=os.environ.copy(), **group) as process:
            try:
                output, _ = process.communicate(timeout=seconds)
                return HostRun(process.returncode, output)
            except subprocess.TimeoutExpired:
                kill_tree(process)
                try:
                    output, _ = process.communicate(timeout=30)
                except subprocess.TimeoutExpired:
                    output = "(the output pipe stayed open after the tree was killed)"
                return HostRun(None, output)


def kill_tree(process: subprocess.Popen) -> None:
    if sys.platform.startswith("win"):
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(process.pid)], capture_output=True, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    process.kill()


@dataclass
class World:
    console: Console
    compose: Compose
    ports: Ports
    hosts: Hosts
    say: Callable[[str], None] = print
    clock: Callable[[], float] = time.monotonic
    sleep: Callable[[float], None] = time.sleep
    notes: dict[str, object] = field(default_factory=dict)

    def wait(self, predicate: Callable[[], tuple[bool, str]], seconds: float, what: str) -> float:
        return wait_until(predicate, seconds, what, clock=self.clock, sleep=self.sleep)
