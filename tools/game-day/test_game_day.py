"""The runner and every scenario's check, against fakes: no stack, no Docker, no host. What it proves is that a
check reads the field the console's contract names and calls a wrong answer a finding; whether the console gives the
right answer is the real run's (README.md, *What a pass proves*)."""

from __future__ import annotations

import io
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import game_day  # noqa: E402
import harness  # noqa: E402
from scenarios import (broker_down, frontend_killed, grafana_down, keycloak_down, service_paused,  # noqa: E402
                       service_stopped, sibling_dirs)


def done(stdout: str = "", returncode: int = 0, stderr: str = "") -> subprocess.CompletedProcess:
    return subprocess.CompletedProcess([], returncode, stdout, stderr)


class FakeConsole:
    """`/api` as a dict of path to body; a body may be a callable, read on every call, so a wait can see it change."""

    def __init__(self, routes: dict) -> None:
        self.routes = routes
        self.calls: list[tuple[str, str]] = []

    def call(self, method: str, path: str, body: object | None = None) -> tuple[int, object]:
        self.calls.append((method, path))
        answer = self.routes[(method, path)]
        return answer() if callable(answer) else answer

    get = harness.Console.get
    stack = harness.Console.stack
    service = harness.Console.service
    reach = harness.Console.reach


class FakeCompose:
    def __init__(self) -> None:
        self.calls: list[tuple[str, str]] = []

    def __getattr__(self, verb: str):
        return lambda service: self.calls.append((verb, service))


class FakePorts:
    def __init__(self, held: list[int] | None = None) -> None:
        self.held = list(held or [])
        self.killed: list[int] = []

    def listeners(self, port: int) -> list[int]:
        return list(self.held)

    def kill(self, pid: int) -> None:
        self.killed.append(pid)
        self.held.remove(pid)


class FakeHosts:
    def __init__(self, runs: dict[str, harness.HostRun]) -> None:
        self.runs = runs

    def start_expecting_refusal(self, args: list[str], seconds: float) -> harness.HostRun:
        key = args[0].split("=")[0].removeprefix("--Admin:")
        return self.runs[key]


def world(routes: dict, ports: FakePorts | None = None, hosts: FakeHosts | None = None) -> harness.World:
    clock = iter(range(0, 100_000, 5))
    return harness.World(console=FakeConsole(routes), compose=FakeCompose(), ports=ports or FakePorts(),
                         hosts=hosts or FakeHosts({}), say=lambda _: None, clock=lambda: next(clock),
                         sleep=lambda _: None)


def stack(services=(), reach=(), frontend=None) -> tuple[int, dict]:
    return 200, {"backend": {"reachable": True, "services": list(services)}, "reachability": list(reach),
                 "frontend": frontend or {"job": None, "installed": True}}


class TheScenarioList(unittest.TestCase):
    def test_every_module_under_scenarios_is_run(self):
        # A scenario file the runner does not list is a failure nobody causes.
        files = {p.stem for p in (Path(game_day.__file__).parent / "scenarios").glob("*.py")
                 if not p.stem.startswith("_")}
        self.assertEqual(files, set(game_day.SCENARIOS))

    def test_every_scenario_has_the_shape_the_runner_calls(self):
        names = []
        for scenario in game_day.load_all():
            for attribute in game_day.REQUIRED:
                self.assertTrue(hasattr(scenario, attribute), f"{scenario.__name__} lacks {attribute}")
            names.append(scenario.NAME)
        self.assertEqual(len(names), len(set(names)))

    def test_list_names_each_scenario(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(game_day.main(["--list"]), 0)
        self.assertEqual(len(out.getvalue().splitlines()), len(game_day.SCENARIOS))

    def test_an_unknown_scenario_is_refused(self):
        with redirect_stdout(io.StringIO()):
            self.assertEqual(game_day.main(["no-such"], world=world({})), 2)


class Scripted:
    NAME = "scripted"
    CLAIM = "a scenario the tests drive"
    NEEDS_STACK = True
    SETTLE_SECONDS = 10

    def __init__(self, *, settled=True, cause_raises=False, findings=(), restore_raises=False) -> None:
        self._settled = settled
        self._cause_raises = cause_raises
        self._findings = list(findings)
        self._restore_raises = restore_raises
        self.done: list[str] = []

    def settled(self, world):
        return self._settled or "restore" in self.done, "scripted"

    def cause(self, world):
        self.done.append("cause")
        if self._cause_raises:
            raise harness.GameDayError("half-way")

    def check(self, world):
        self.done.append("check")
        return list(self._findings)

    def restore(self, world):
        self.done.append("restore")
        if self._restore_raises:
            raise harness.GameDayError("would not come back")


class TheRunner(unittest.TestCase):
    def test_a_clean_run_causes_checks_and_restores(self):
        scenario = Scripted()
        self.assertEqual(game_day.run(scenario, world({})), [])
        self.assertEqual(scenario.done, ["cause", "check", "restore"])

    def test_a_cause_that_raises_is_still_restored(self):
        scenario = Scripted(cause_raises=True)
        findings = game_day.run(scenario, world({}))
        self.assertEqual(scenario.done, ["cause", "restore"])
        self.assertEqual(findings, ["scripted: the cause or the check failed: GameDayError: half-way"])

    def test_any_raise_is_a_finding_and_all_goes_on_to_the_next(self):
        # No `docker` or `lsof` on PATH, or a body missing its field: the run must still report and carry on.
        class Missing(Scripted):
            def cause(self, world):
                self.done.append("cause")
                raise FileNotFoundError("lsof")

        first, second = Missing(), Scripted()
        out = io.StringIO()
        original = game_day.load_all
        game_day.load_all = lambda: [first, second]
        try:
            with redirect_stdout(out):
                code = game_day.main(["all"], world=world({}))
        finally:
            game_day.load_all = original
        self.assertEqual(code, 1)
        self.assertIn("FINDING: scripted: the cause or the check failed: FileNotFoundError: lsof", out.getvalue())
        self.assertEqual(first.done, ["cause", "restore"])
        self.assertEqual(second.done, ["cause", "check", "restore"])

    def test_an_unsettled_start_causes_nothing(self):
        scenario = Scripted(settled=False)
        findings = game_day.run(scenario, world({}))
        self.assertEqual(scenario.done, [])
        self.assertIn("not starting from a settled state", findings[0])

    def test_a_restore_that_fails_is_a_finding(self):
        findings = game_day.run(Scripted(restore_raises=True), world({}))
        self.assertIn("the restore did not settle", findings[0])

    def test_the_halves_run_apart(self):
        cause_only, restore_only = Scripted(), Scripted()
        game_day.run(cause_only, world({}), restore=False)
        game_day.run(restore_only, world({}), cause=False)
        self.assertEqual(cause_only.done, ["cause", "check"])
        self.assertEqual(restore_only.done, ["restore"])

    def test_findings_print_as_finding_lines_and_fail_the_run(self):
        out = io.StringIO()
        original = game_day.by_name
        game_day.by_name = lambda name: Scripted(findings=["the screen lied"])
        try:
            with redirect_stdout(out):
                code = game_day.main(["scripted"], world=world({}))
        finally:
            game_day.by_name = original
        self.assertEqual(code, 1)
        self.assertIn("FINDING: scripted: the screen lied", out.getvalue())


def row(state: str, health: str | None = None) -> dict:
    return {"service": "catalog-api", "state": state, "health": health}


def probe(name: str, up: bool, url: str = "http://localhost:1") -> dict:
    return {"name": name, "url": url, "up": up, "status": None}


class TheComposeRows(unittest.TestCase):
    def test_a_stopped_service_shown_exited_and_down_passes(self):
        w = world({("GET", "/api/stack"): stack([row("exited")], [probe("catalog", False)])})
        service_stopped.cause(w)
        self.assertEqual(service_stopped.check(w), [])
        self.assertEqual(w.compose.calls, [("stop", "catalog-api")])

    def test_a_stopped_service_still_shown_running_is_a_finding(self):
        w = world({("GET", "/api/stack"): stack([row("running")], [probe("catalog", True)])})
        self.assertIn("catalog-api state=running", service_stopped.check(w)[0])

    def test_a_paused_service_must_read_paused_not_running(self):
        paused = world({("GET", "/api/stack"): stack([row("paused")], [probe("catalog", False)])})
        running = world({("GET", "/api/stack"): stack([row("running")], [probe("catalog", False)])})
        self.assertEqual(service_paused.check(paused), [])
        self.assertTrue(service_paused.check(running))

    def test_settled_needs_running_healthy_and_up(self):
        healthy = world({("GET", "/api/stack"): stack([row("running", "healthy")], [probe("catalog", True)])})
        starting = world({("GET", "/api/stack"): stack([row("running", "starting")], [probe("catalog", True)])})
        self.assertTrue(service_stopped.settled(healthy)[0])
        self.assertFalse(service_stopped.settled(starting)[0])


class TheObservability(unittest.TestCase):
    def routes(self, reachable: bool, grafana_up: bool) -> dict:
        view = (200, {"reachable": reachable, "error": None if reachable else "Connection refused"})
        return {("GET", "/api/telemetry/health"): view, ("GET", "/api/telemetry/alerts"): view,
                ("GET", grafana_down.TRACE): view, ("GET", "/api/stack"): stack(reach=[probe("grafana", grafana_up)])}

    def test_every_read_unreachable_with_an_error_passes(self):
        self.assertEqual(grafana_down.check(world(self.routes(False, False))), [])

    def test_a_read_that_stays_reachable_is_a_finding_naming_it(self):
        findings = grafana_down.check(world(self.routes(True, False)))
        self.assertEqual(len(findings), 3)
        self.assertIn("/api/telemetry/health", findings[0])

    def test_unreachable_without_an_error_is_a_finding(self):
        routes = self.routes(False, False)
        routes[("GET", "/api/telemetry/alerts")] = (200, {"reachable": False, "error": None})
        self.assertEqual(len(grafana_down.check(world(routes))), 1)

    def test_a_500_raises_for_the_runner_to_report(self):
        # The runner turns a GameDayError from a check into a finding (TheRunner); a 500 must not read as a pass.
        routes = self.routes(False, False)
        routes[("GET", grafana_down.TRACE)] = (500, "boom")
        with self.assertRaisesRegex(harness.GameDayError, "answered 500"):
            grafana_down.check(world(routes))


class TheKeycloak(unittest.TestCase):
    def routes(self, answer: tuple[int, object]) -> dict:
        return {("POST", "/api/identity/token"): answer, ("GET", "/api/stack"): stack(reach=[probe("keycloak", False)])}

    def test_a_502_titled_as_promised_passes(self):
        self.assertEqual(keycloak_down.check(world(self.routes((502, {"title": keycloak_down.TITLE})))), [])

    def test_any_other_answer_is_a_finding(self):
        self.assertTrue(keycloak_down.check(world(self.routes((500, {"title": "Internal"})))))
        self.assertTrue(keycloak_down.check(world(self.routes((401, "refused")))))

    def test_the_identity_is_never_a_cached_one(self):
        self.assertNotEqual(keycloak_down._identity(), keycloak_down._identity())


class TheBroker(unittest.TestCase):
    def view(self, reachable: bool, found: bool, drained: bool | None = None, error: str | None = "not running"):
        return {("GET", "/api/broker/queues"): (200, {"reachable": reachable, "error": error, "queues": [],
                                                       "projection": {"found": found, "drained": drained}})}

    def test_unreachable_and_not_found_passes(self):
        self.assertEqual(broker_down.check(world(self.view(False, False))), [])

    def test_a_projection_read_as_drained_is_a_finding(self):
        self.assertTrue(broker_down.check(world(self.view(False, False, drained=True))))

    def test_reachable_while_stopped_is_a_finding(self):
        self.assertTrue(broker_down.check(world(self.view(True, True, error=None))))


class TheSiblingDirs(unittest.TestCase):
    def check(self, backend: harness.HostRun, frontend: harness.HostRun) -> list[str]:
        return sibling_dirs.check(world({}, hosts=FakeHosts({"BackendDir": backend, "FrontendDir": frontend})))

    def test_a_refusal_naming_the_key_passes(self):
        self.assertEqual(self.check(harness.HostRun(134, "Admin:BackendDir resolves to 'x', which does not exist."),
                                    harness.HostRun(134, "Admin:FrontendDir resolves to 'y', which has no package.json.")),
                         [])

    def test_a_host_that_starts_or_exits_clean_or_names_nothing_is_a_finding(self):
        findings = self.check(harness.HostRun(None, "Now listening"), harness.HostRun(0, ""))
        self.assertEqual(len(findings), 2)
        self.assertEqual(len(self.check(harness.HostRun(1, "Unhandled exception"),
                                        harness.HostRun(1, "Admin:FrontendDir resolves"))), 1)


class TheFrontend(unittest.TestCase):
    def test_a_killed_dev_server_reads_exited_non_zero_and_stop_frees_the_port(self):
        ports = FakePorts([])
        state = {"job": None}

        def read_stack():
            return stack(reach=[probe("client", True, "http://localhost:5173/")],
                         frontend={"job": state["job"], "installed": True})

        def start():
            ports.held = [4343]
            return 202, {}

        def stop():
            ports.held = []
            return 200, {}

        w = world({("GET", "/api/stack"): read_stack, ("POST", "/api/stack/frontend/start"): start,
                   ("POST", "/api/stack/frontend/stop"): stop}, ports=ports)
        frontend_killed.cause(w)
        self.assertEqual(ports.killed, [4343])
        state["job"] = {"state": "Exited", "exitCode": 137}
        self.assertEqual(frontend_killed.check(w), [])
        self.assertEqual(w.notes["port"], 5173)

    def test_a_job_still_running_or_exiting_zero_is_a_finding(self):
        for job in ({"state": "Running", "exitCode": None}, {"state": "Exited", "exitCode": 0}):
            ports = FakePorts([])
            w = world({("GET", "/api/stack"): stack(reach=[probe("client", True, "http://localhost:5173/")],
                                                    frontend={"job": job, "installed": True}),
                       ("POST", "/api/stack/frontend/start"): lambda: (ports.held.append(1) or (202, {})),
                       ("POST", "/api/stack/frontend/stop"): lambda: (ports.held.clear() or (200, {}))}, ports=ports)
            w.notes["port"] = 5173
            self.assertTrue(frontend_killed.check(w), job)

    def test_a_job_still_running_keeps_its_finding_and_skips_the_second_start(self):
        # The console refuses a second start with a 409 while it holds the first; that refusal must not replace the
        # finding about the job, which is the one a reader needs.
        w = world({("GET", "/api/stack"): stack(reach=[probe("client", True, "http://localhost:5173/")],
                                                frontend={"job": {"state": "Running"}, "installed": True}),
                   ("POST", "/api/stack/frontend/start"): (409, {"title": "Frontend already running"})})
        w.notes["port"] = 5173
        findings = frontend_killed.check(w)
        self.assertEqual(len(findings), 2)
        self.assertIn("reading Exited", findings[0])
        self.assertIn("tree-kill half was not run", findings[1])
        self.assertNotIn(("POST", "/api/stack/frontend/start"), w.console.calls)

    def test_a_stop_that_leaves_the_port_held_is_the_tree_kill_finding(self):
        ports = FakePorts([])
        w = world({("GET", "/api/stack"): stack(reach=[probe("client", True, "http://localhost:5173/")],
                                                frontend={"job": {"state": "Exited", "exitCode": 1}, "installed": True}),
                   ("POST", "/api/stack/frontend/start"): lambda: (ports.held.append(7) or (202, {})),
                   ("POST", "/api/stack/frontend/stop"): (200, {})}, ports=ports)
        w.notes["port"] = 5173
        findings = frontend_killed.check(w)
        self.assertEqual(len(findings), 1)
        self.assertIn("tree kill", findings[0])

    def routes_for_a_server_that_never_listens(self, job: dict) -> dict:
        return {("GET", "/api/stack"): stack(reach=[probe("client", False, "http://localhost:5173/")],
                                             frontend={"job": job, "installed": True}),
                ("POST", "/api/stack/frontend/start"): (202, {"id": "j1"}),
                ("GET", "/api/jobs/j1"): (200, {"summary": job, "lines": [{"text": "Error: Cannot find module"}]})}

    def test_npm_gone_before_listening_says_so_with_its_output(self):
        w = world(self.routes_for_a_server_that_never_listens({"id": "j1", "state": "Exited", "exitCode": 1}))
        with self.assertRaisesRegex(harness.GameDayError, "exited 1 before listening.*Cannot find module"):
            frontend_killed.cause(w)

    def test_a_server_still_compiling_at_the_deadline_shows_its_output(self):
        w = world(self.routes_for_a_server_that_never_listens({"id": "j1", "state": "Running", "exitCode": None}))
        with self.assertRaisesRegex(harness.Timeout, "job=Running.*last lines: Error: Cannot find module"):
            frontend_killed.cause(w)

    def test_an_uninstalled_clone_is_not_a_settled_start(self):
        w = world({("GET", "/api/stack"): stack(reach=[probe("client", False, "http://localhost:5173/")],
                                                frontend={"job": None, "installed": False, "installProblem": "no node_modules"})})
        ok, seen = frontend_killed.settled(w)
        self.assertFalse(ok)
        self.assertIn("no node_modules", seen)


class TheHarness(unittest.TestCase):
    def test_a_timeout_names_what_was_last_seen(self):
        clock = iter(range(0, 1000, 10))
        with self.assertRaisesRegex(harness.Timeout, "last seen: still 3"):
            harness.wait_until(lambda: (False, "still 3"), 30, "x", clock=lambda: next(clock), sleep=lambda _: None)

    def test_compose_state_reads_lines_and_arrays(self):
        lines = harness.Compose(Path("f.yml"), run=lambda args: done('{"Service":"a","State":"paused"}\n'))
        array = harness.Compose(Path("f.yml"), run=lambda args: done('[{"Service":"a","State":"exited"}]'))
        empty = harness.Compose(Path("f.yml"), run=lambda args: done(""))
        self.assertEqual((lines.state("a"), array.state("a"), empty.state("a")), ("paused", "exited", "absent"))

    def test_compose_names_the_file_and_fails_loudly(self):
        seen = []
        compose = harness.Compose(Path("f.yml"), run=lambda args: seen.append(args) or done(returncode=1, stderr="no"))
        with self.assertRaisesRegex(harness.GameDayError, "docker compose stop x exited 1: no"):
            compose.stop("x")
        self.assertEqual(seen[0][:4], ["docker", "compose", "-f", "f.yml"])

    def test_a_host_that_starts_is_killed_as_a_tree_and_the_read_ends(self):
        # `dotnet run`'s shape: a parent that starts a child holding the same output pipe, and both stay up.
        child = "import time; time.sleep(600)"
        parent = (f"import subprocess, sys, time; subprocess.Popen([sys.executable, '-c', {child!r}]); "
                  "print('Now listening', flush=True); time.sleep(600)")
        started = time.monotonic()
        run = harness.Hosts([sys.executable, "-c", parent]).start_expecting_refusal([], 3)
        self.assertLess(time.monotonic() - started, 45)
        self.assertIsNone(run.exit_code)
        self.assertIn("Now listening", run.output)

    def test_a_linked_worktree_resolves_to_its_main_checkout(self):
        with tempfile.TemporaryDirectory() as temp:
            main = Path(temp) / "admin"
            linked = main / ".claude" / "worktrees" / "x"
            git_dir = main / ".git" / "worktrees" / "x"
            git_dir.mkdir(parents=True)
            linked.mkdir(parents=True)
            (git_dir / "commondir").write_text("../..\n", encoding="utf-8")
            (linked / ".git").write_text(f"gitdir: {git_dir}\n", encoding="utf-8")
            self.assertEqual(harness.main_checkout(linked), main.resolve())
            self.assertEqual(harness.main_checkout(main), main)

    def test_ports_read_pids_on_every_platform(self):
        # ss's line as a GitHub runner prints it for `ng serve` on ::1 and 127.0.0.1.
        ss = ('LISTEN 0 511 [::1]:5173 [::]:* users:(("node",pid=101,fd=40))\n'
              'LISTEN 0 511 127.0.0.1:5173 0.0.0.0:* users:(("node",pid=202,fd=41),("node",pid=101,fd=42))\n')
        seen = []
        linux = harness.Ports(run=lambda args: seen.append(args) or done(ss), platform="linux")
        windows = harness.Ports(run=lambda args: done("0\r\n303\r\n303\r\n"), platform="win32")
        mac = harness.Ports(run=lambda args: done("404\n"), platform="darwin")
        self.assertEqual((linux.listeners(5173), windows.listeners(1), mac.listeners(1)), ([101, 202], [303], [404]))
        self.assertEqual(seen[0], ["ss", "-Hltnp", "sport = :5173"])

    def test_a_listener_read_that_fails_says_so_rather_than_reading_empty(self):
        broken = harness.Ports(run=lambda args: done("", 1, "Cannot open netlink socket"), platform="linux")
        nobody = harness.Ports(run=lambda args: done("", 0, ""), platform="linux")
        with self.assertRaisesRegex(harness.GameDayError, "netlink"):
            broken.listeners(5173)
        self.assertEqual(nobody.listeners(5173), [])


if __name__ == "__main__":
    unittest.main()
