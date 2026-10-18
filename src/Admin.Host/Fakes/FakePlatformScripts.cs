using System.Reflection;
using Admin.Host.Compose;
using Admin.Host.Stack;

namespace Admin.Host.Fakes;

/// <summary>The recorded docker outputs FakePlatform mode replays. Exact text is not a contract; the shapes are.</summary>
public static class FakePlatformScripts
{
    // logs -f output as Compose prefixes it. By service: gateway 3, catalog-api 4,
    // ordering-api 3, web-bff 1, rabbitmq 1; twelve in all. A follow of named
    // services replays only their lines, as docker compose logs does.
    private static readonly string[] FollowLogLines =
    [
        "gateway       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "catalog-api   | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "ordering-api  | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "web-bff       | info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080",
        "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0001",
        "catalog-api   | info: Catalog.Api.Endpoints[1] Listed 20 products CorrelationId=fake-corr-0001",
        "gateway       | info: Yarp.ReverseProxy.Forwarder.HttpForwarder[9] Proxying to http://catalog-api:8080/v1/catalog/products CorrelationId=fake-corr-0002",
        "catalog-api   | info: Catalog.Api.Endpoints[2] Published product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f CorrelationId=fake-corr-0002",
        "catalog-api   | info: Common.Infrastructure.Outbox.OutboxDispatcher[10] Dispatched PriceChanged to catalog-events CorrelationId=fake-corr-0002",
        "ordering-api  | info: MassTransit[0] Consumed PriceChanged on ordering-catalog-events CorrelationId=fake-corr-0002",
        "ordering-api  | info: Ordering.Application.Projections[3] Priced product 0199a3f0-4b1c-7d2e-9f10-2a3b4c5d6e7f at 19.99 EUR",
        "rabbitmq      | 2026-09-14 07:00:00.000 [info] <0.1.0> connection accepted from ordering-api",
    ];

    // npm start in the frontend clone as the Angular dev server prints it. It
    // keeps running until stopped, as ng serve does.
    private static readonly string[] NgServeLines =
    [
        "> blueprint-frontend@0.0.0 start",
        "> ng serve",
        "Initial chunk files | Names  | Raw size",
        "main.js             | main   | 212.40 kB",
        "styles.css          | styles |  95.12 kB",
        "Application bundle generation complete. [2.315 seconds]",
        "Watch mode enabled. Watching for file changes...",
        "  Local:   http://localhost:5173/",
    ];

    /// <summary>
    /// Every command FakePlatform answers. The workstation doctor's reads replay what this workstation answered on
    /// 2026-10-04 with the stack up, except the two ports a host RabbitMQ held the day before, which keep its
    /// answer so the panel has its red row (<see cref="WorkstationRecordings.Listeners"/>). compose-ps.jsonl is
    /// <c>ps</c> as <c>Admin:Record</c> wrote it from the whole stack (<see cref="FixtureRecordings"/>), so the doctor
    /// finds every port Docker holds published by a container of this stack.
    /// </summary>
    public static FakeProcessRunner Script(FakeProcessRunner runner, Config.RepoPaths paths, FakeStack? stack = null)
    {
        stack ??= new FakeStack();
        string prefix = $"compose -f {paths.ComposeFile} ";

        return runner
            .On("docker", prefix + "ps -a --format json", 0, [.. ComposePsLines()])
            .On("docker", prefix + "up -d --wait", 0,
                " Network commerce_default  Created",
                " Container commerce-sql-1  Healthy",
                " Container commerce-rabbitmq-1  Healthy",
                " Container commerce-keycloak-1  Healthy",
                " Container commerce-catalog-api-1  Healthy",
                " Container commerce-gateway-1  Healthy")
            .On("docker", prefix + "down -v", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Volume commerce_sql-data  Removed",
                " Volume commerce_rabbit-data  Removed",
                " Network commerce_default  Removed")
            .On("docker", prefix + "down", 0,
                " Container commerce-gateway-1  Removed",
                " Container commerce-catalog-api-1  Removed",
                " Container commerce-sql-1  Removed",
                " Network commerce_default  Removed")
            // A Scenario's lease (spec §5.3): the two changes and the one restore each. The restore's verification reads the
            // recorded ps, which keeps both services running. What each command did is remembered in FakeStack, so an order
            // placed after it ends as it would on a stack.
            .On("docker", prefix + "stop inventory-api", 0, _ =>
            {
                stack.InventoryStopped = true;

                return [" Container commerce-inventory-api-1  Stopped"];
            })
            .On("docker", prefix + "up -d --no-deps --wait inventory-api", 0, _ =>
            {
                stack.InventoryStopped = false;

                return [" Container commerce-inventory-api-1  Healthy"];
            })
            .On("docker", $"compose -f {paths.ComposeFile} -f {ComposeService.ShortTimeoutsFile(paths.ComposeFile)} up -d --no-deps --wait ordering-api", 0, _ =>
            {
                stack.ShortWaits = true;

                return [" Container commerce-ordering-api-1  Healthy"];
            })
            .On("docker", prefix + "up -d --no-deps --wait ordering-api", 0, _ =>
            {
                stack.ShortWaits = false;

                return [" Container commerce-ordering-api-1  Healthy"];
            })
            .On("docker", prefix + "stop web-bff", 0, Lines(ReplayRecordings.StopBff))
            .On("docker", prefix + "start web-bff", 0, Lines(ReplayRecordings.StartBff))
            .On("dotnet", "run --project tools/bff-replay -- --reset", 0, Lines(ReplayRecordings.Rebuild))
            .On("dotnet", "run --project tools/bff-replay", 0, Lines(ReplayRecordings.Repair))
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_queues name messages --formatter json", 0, [.. FixtureLines("rabbitmq-queues.json")])
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_exchanges name type --formatter json", 0, [.. FixtureLines("rabbitmq-exchanges.json")])
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_permissions --formatter json", 0, [.. FixtureLines("rabbitmq-permissions.json")])
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl change_password dead-letter-operator ", 0, "Changing password for user \"dead-letter-operator\" ...")
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl clear_password dead-letter-operator", 0, "Clearing password for user \"dead-letter-operator\" ...")
            .On("py", DeadLetterRecordings.Prefix + "inspect ", 0, DeadLetterRecordings.Inspect)
            .On("py", DeadLetterRecordings.Prefix + "replay ", 0, rest => DeadLetterRecordings.Move(["replay", .. rest]))
            .On("py", DeadLetterRecordings.Prefix + "discard ", 0, rest => DeadLetterRecordings.Move(["discard", .. rest]))
            // The game day (run-locally.md, "Game day"): the listing first, since the cause and restore prefix would also match it.
            .On("py", RunbookRecordings.Prefix + "--list --json", 0, Lines(RunbookRecordings.Catalogue))
            .On("py", RunbookRecordings.Prefix, 0, rest => RunbookRecordings.Run(rest, stack))
            .OnLongRunning("docker", prefix + "logs -f --tail 200", services => FollowLogLines.Where(l => services.Count == 0 || services.Contains(ServiceOf(l))))
            .On("docker", prefix + "config --format json", 0, Lines(WorkstationRecordings.ComposeConfig))
            .On("docker", prefix + "images --format json", 0, Lines(WorkstationRecordings.ComposeImages))
            .On("docker", "info --format {{.ServerVersion}}", 0, "29.7.2")
            .On("powershell", "-NoProfile -Command Get-NetTCPConnection ", 0, Lines(WorkstationRecordings.Listeners))
            .On("powershell", "-NoProfile -Command Get-Content ", 0, "22.23.2")
            .On("node", "--version", 0, "v22.23.2")
            .On("py", "-3.12 --version", 0, "Python 3.12.7")
            .On("git", $"-C {paths.BackendDir} status -sb", 0, "## main...origin/main")
            .On("git", $"-C {paths.FrontendDir} status -sb", 0, "## main...origin/main")
            .On("git", $"-C {paths.BackendDir} reflog -1 --date=iso-strict --format=%gd", 0, "HEAD@{2026-10-04T06:36:23+05:00}")
            // The load run (run-locally.md, "Load run (k6)"): each block as a live run of it printed, the k6 read also being the
            // doctor's. Six blocks open on the same lines, so each is matched whole. The run's block puts the strip under load.
            .On("powershell", PowerShell(LoadRunService.K6Read), 0, Recorded(LoadRunRecordings.K6))
            .On("powershell", PowerShell(LoadRunService.PrometheusReady), 0, Recorded(LoadRunRecordings.Prometheus))
            .On("powershell", PowerShell(LoadRunService.SloClient), 0, Recorded(LoadRunRecordings.SloClient))
            .On("powershell", PowerShell(LoadRunService.TokenCheck), 0, Recorded(LoadRunRecordings.TokenCheck))
            .On("powershell", PowerShell(LoadRunService.Product), 0, Recorded(LoadRunRecordings.Product))
            .On("powershell", PowerShell(LoadRunService.Restock), 0, Recorded(LoadRunRecordings.Restock))
            .On("powershell", PowerShell(LoadRunService.Run), 0, _ =>
            {
                stack.Load();

                return Recorded(LoadRunRecordings.Run);
            })
            .On("powershell", "-NoProfile -Command & ", 0, DeviceRecordings.AdbDevices)
            .On("powershell", "-NoProfile -Command curl.exe ", 0, DeviceRecordings.Preflight)
            .On("powershell", "-NoProfile -Command $t = ", 0, DeviceRecordings.Realm)
            .On("npm", "run emulator:android", 0, DeviceRecordings.Build)
            .OnLongRunning("npm", "start", NgServeLines);
    }

    public static IReadOnlyList<string> ComposePsLines() => FixtureLines("compose-ps.jsonl");

    private static string[] Lines(string recording) =>
        recording.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The arguments the host gives <c>powershell</c> for one block (<see cref="LoadRunService.Arguments"/>), joined as the runner joins them.</summary>
    private static string PowerShell(string block) => string.Join(' ', LoadRunService.Arguments(block));

    /// <summary>A recording one line to an entry as it printed, its indentation and blank lines kept, whatever this file's line ending.</summary>
    private static string[] Recorded(string recording) => recording.ReplaceLineEndings("\n").Split('\n');

    /// <summary>A recorded output embedded under its logical name, one entry per non-empty line.</summary>
    public static IReadOnlyList<string> FixtureLines(string logicalName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The Compose prefix of a log line: the service name before the <c>|</c>.</summary>
    private static string ServiceOf(string line) => line[..line.IndexOf('|', StringComparison.Ordinal)].Trim();
}
