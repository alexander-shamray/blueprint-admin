using System.Text.RegularExpressions;
using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Admin.Host.Runbooks;
using Admin.Host.Stack;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The converse of "every command the host runs is a line from run-locally.md": every line in that
/// document's fenced blocks is a command the host can be shown to run, a request to a host the
/// console configures, a command listed here as deliberately not run, or PowerShell that only builds
/// a value. A new line of any other shape fails until someone decides which it is.
/// </summary>
public sealed partial class RunLocallyCommandsDriftTests : IAsyncDisposable
{
    /// <summary>
    /// The clones and the compose file as run-locally.md spells them from the workspace root, so the host's argv and
    /// the document's compare token for token.
    /// </summary>
    private static readonly RepoPaths Paths = new("blueprint-backend", "blueprint-frontend", new AdminOptions().ComposeFile);

    /// <summary>Commands in the document the console does not run, and why.</summary>
    private static readonly Dictionary<string, string> NotRun = new(StringComparer.Ordinal)
    {
        ["npm ci"] = "a prerequisite the console reports rather than runs (FrontendSupervisor, spec §2.1)",
        ["py -3.12 tools/dead-letters/dead_letters.py list"] =
            "the Broker screen's queue listing already gives each parked queue and its depth (BrokerService, ParkedQueues)",
        ["docker compose -f deploy/compose/docker-compose.yml exec sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Shipping -W -Q \"select Status, UnfulfillableReason, Attempts, CancellationRefusedAt from shipping.Shipments where OrderId = '$orderId'\""] =
            "a by-hand read of a table the console does not query; the cause scripts that need it wait on #67",
        ["docker compose -f deploy/compose/docker-compose.yml exec sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -W -Q \"select Reason from ordering.OrderReviews where OrderId = '$orderId'\""] =
            "a by-hand read of the queue table the console does not query; the Scenario reads the log line the document gives beside it (ReviewFlagService)",
        ["docker compose -f deploy/compose/docker-compose.yml start inventory-api"] =
            "a Scenario's lease restores Inventory with up -d --no-deps, which the document says does the same (ScenarioControl.Table)",
        ["docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.short-timeouts.yml up -d --wait"] =
            "the full-stack up with the override added, which a developer runs by hand; the host's Up never adds it, and a Scenario's lease recreates Ordering alone",
        ["docker compose -f deploy/compose/docker-compose.yml exec sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -W -Q \"select Status, CancelReason from ordering.OrderSummaries where OrderId = '$orderId'\""] =
            "a by-hand read of the cancel reason, which the order read folds; the console does not read that table",
        [@"& ""$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"" reverse tcp:8080 tcp:8080"] =
            "it changes the emulator's forwarding, and the device lane is reads and one build (spec §5.4)",
        [@"& ""$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"" reverse --list"] = "the read-back of the line above, which is not run",
        [@".\gradlew.bat installDebug"] = "the install is by hand: the lane builds and syncs, and the device is the caller's (spec §5.4)",
        [@"& ""$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"" shell am start -n dev.ashamray.blueprint/.MainActivity"] =
            "starting the app is by hand, with the install above",
        ["'slo-run exists: ' + $(if ($found.Count -gt 0) { 'yes' } else { 'no' })"] =
            "the load run's client block reads the same and says created or already there (LoadRunService.SloClient)",
        ["if (-not $c) { 'slo-run: missing (create it first)'; exit 1 }"] =
            "the secret read prints the secret, and the load run's own block reads it into k6's environment (LoadRunService.Run)",
        ["} else { 'slo-run: already there' }"] =
            "the other branch of the load run's client block, which says created or already there (LoadRunService.SloClient)",
    };

    /// <summary>
    /// Lines of "Attach a product image by hand" that the host performs in code, not as a command it starts or a request the
    /// proxy forwards, each with the code that does and the test that holds it to the document: the length of the file
    /// (<see cref="SampleImage.SizeBytes"/>), the upload of its bytes (<see cref="ProductImageUploader"/>, which the proxy cannot
    /// do with a string body), the read of the thumbnail from the edge (the Scenario's <c>thumbnail</c> step, a HEAD through
    /// the proxy to a URL the product returned), and the two lines of the signing and publishing calls whose hashtables
    /// <c>RunLocallyDriftTests</c> reads.
    /// </summary>
    private static readonly Dictionary<string, string> Accounted = new(StringComparer.Ordinal)
    {
        [@"$size = (Get-Item .\desk.png).Length"] = "SampleImage.SizeBytes",
        ["-ContentType 'application/json' -Body (@{ contentType = 'image/png'; sizeBytes = $size } | ConvertTo-Json)"] = "RunLocallyExamples.ProductImageUpload, held by RunLocallyDriftTests",
        [@"Invoke-RestMethod $up.uploadUrl -Method Put -InFile .\desk.png -ContentType $up.headers.'Content-Type'"] = "ProductImageUploader",
        ["-ContentType 'application/json' -Body (@{"] = "the publish body the Scenario sends with imageKey, held by RunLocallyDriftTests",
        ["Invoke-WebRequest $p.thumbnailUrl -Method Head"] = "the Scenario's thumbnail step, a HEAD through the proxy",
        ["{service_name=\"Ordering.Api\"} |= \"flagged for review\" |= \"<order id>\""] =
            "the Scenario's review step reads Loki with it (ReviewFlagService.LogQl, held by RunLocallyDriftTests)",
        ["$env:DEAD_LETTERS_PASSWORD = [guid]::NewGuid().ToString('N')"] =
            "DeadLetterService mints a random password for each run and hands it to the tool as this variable",
        ["$c.redirectUris; '--- webOrigins ---'; $c.webOrigins -join ','"] =
            "the third line of the realm read, run with the two lines above it as one script (DeviceReads.RealmLines, held by DeviceReadsTests)",
        ["$feed = \"/v1/shipments/crr_SIM-STRANGE-$orderId/events\""] =
            "the path the feed read matches, which the Scenario fills with its own order's id (RunLocallyExamples.CarrierFeedPolls, held by RunLocallyDriftTests)",
        ["Remove-Item Env:DEAD_LETTERS_PASSWORD"] =
            "the variable is set on the tool's own process only (ProcessSpec.Environment), so it ends with that process",
    };

    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public RunLocallyCommandsDriftTests() => runner = new FakeProcessRunner(new JobRegistry(time));

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task Every_fenced_line_in_run_locally_is_run_by_the_host_or_accounted_for()
    {
        string[] lines = FencedLines(File.ReadAllText(Backend.RunLocally));
        IReadOnlyList<string[]> host = await HostCommandsAsync();
        HashSet<string> services = [.. Compose.Services().Select(s => s.Name)];
        HashSet<string> configured = [.. Urls(new AdminOptions()).Select(u => new Uri(u).Authority)];
        List<string> unaccounted = [];

        lines.ShouldContain(l => Command().IsMatch(l), "no fenced command was read; the fence reader no longer fits the document");

        foreach (string line in lines.Where(l => !NotRun.ContainsKey(l) && !Accounted.ContainsKey(l)))
        {
            if (host.Any(h => RunsWhole(h, line)))
            {
                continue;
            }

            if (Command().IsMatch(line))
            {
                if (!host.Any(h => h.Contains(line) || Runs(h, Tokens(line), services)))
                {
                    unaccounted.Add($"no host command runs: {line}");
                }
            }
            else if (line.Contains("Invoke-RestMethod", StringComparison.Ordinal))
            {
                Match url = Url().Match(line);

                if (!url.Success || !configured.Contains(new Uri(url.Value).Authority))
                {
                    unaccounted.Add($"a request to a host the console does not configure: {line}");
                }
            }
            else if (!BuildsAValue().IsMatch(line))
            {
                unaccounted.Add($"neither a command, a request nor a value: {line}");
            }
        }

        unaccounted.ShouldBeEmpty();
    }

    [Fact]
    public void Every_listed_exception_is_still_in_the_document()
    {
        string[] lines = FencedLines(File.ReadAllText(Backend.RunLocally));

        NotRun.Keys.Concat(Accounted.Keys).Where(c => !lines.Contains(c)).ShouldBeEmpty();
    }

    [Fact]
    public void A_host_command_with_extra_flags_runs_the_documents_line_and_a_different_verb_does_not()
    {
        string[] host = ["docker", "compose", "-f", "f.yml", "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues", "--formatter", "json"];
        HashSet<string> services = ["rabbitmq"];

        Runs(host, "docker compose -f f.yml exec rabbitmq rabbitmqctl list_queues".Split(' '), services).ShouldBeTrue();
        Runs(host, "docker compose -f f.yml exec rabbitmq rabbitmqctl list_exchanges".Split(' '), services).ShouldBeFalse();
    }

    /// <summary>
    /// The host's own argv for each command it runs, built by the host's own code. Logs follows every
    /// Compose service, the widest selection a Logs widget can be set to.
    /// </summary>
    private async Task<IReadOnlyList<string[]>> HostCommandsAsync()
    {
        ComposeService compose = new(runner, Paths, time);
        BrokerService broker = new(compose, Options.Create(new AdminOptions()));
        using FrontendSupervisor frontend = new(runner, Paths, FrontendInstall.Replayed);

        compose.Up();
        compose.Down(wipeVolumes: false);
        compose.Down(wipeVolumes: true);
        compose.Stop(ProjectionRebuildService.BffService);
        compose.Start(ProjectionRebuildService.BffService);

        // A Scenario's lease: each change the host may make, and the one line that undoes it.
        foreach (ScenarioChange change in ScenarioControl.Table.Values)
        {
            change.Apply(compose);
            change.Restore(compose);
        }

        runner.Start(ProjectionRebuildService.ReplaySpec(reset: false, Paths, new AdminOptions()));
        runner.Start(ProjectionRebuildService.ReplaySpec(reset: true, Paths, new AdminOptions()));

        // The dead-letter lines built by the host's own spec builders with the document's example values in place of
        // the run's: the variable for the password, the queue it names, its --limit 5 (the host reads
        // DeadLetterService.InspectLimit), <id> and its two file names. So what is compared token for token is each
        // line's shape, its verb, flags and their order, which is what the host must not drift from.
        const string Variable = "$env:DEAD_LETTERS_PASSWORD";
        const string Queue = "ordering-catalog-events_error";
        await compose.ExecListedAsync("rabbitmq", DeadLetterService.SetPasswordArguments(Variable), new HashSet<string>(), TestContext.Current.CancellationToken);
        await compose.ExecListedAsync("rabbitmq", DeadLetterService.ClearPasswordArguments, new HashSet<string>(), TestContext.Current.CancellationToken);
        runner.Start(DeadLetterService.InspectSpec(Queue, 5, Paths, Variable));

        foreach (DeadLetterVerb verb in Enum.GetValues<DeadLetterVerb>())
        {
            runner.Start(DeadLetterService.MoveSpec(verb, Queue, ["<id>"], null, Paths, Variable));
            runner.Start(DeadLetterService.MoveSpec(verb, Queue, ["<id>"], ("incident.jsonl", "audit.jsonl"), Paths, Variable));
        }

        // The load run: every block it runs, each whole, as it starts them.
        foreach (LoadRunStep step in LoadRunService.Steps)
        {
            runner.Start(LoadRunService.Spec(step.Block, Paths));
        }

        // The stress run: every block it runs, each whole; its way back is the Up above.
        foreach (LoadRunStep step in StressRunService.Steps.Where(s => s.Name != StressRunService.SettleStep))
        {
            runner.Start(LoadRunService.Spec(step.Block, Paths));
        }

        // The game day: the listing, and each half of a runbook the document names.
        runner.Start(GameDayService.Spec(GameDayService.ListArguments, Paths));

        foreach (GameDayMode mode in Enum.GetValues<GameDayMode>())
        {
            runner.Start(GameDayService.Spec(GameDayService.Arguments("outbox-broker", mode, null), Paths));
        }

        compose.FollowLogs([.. Compose.Services().Select(s => s.Name)]);
        await compose.PsAsync(TestContext.Current.CancellationToken);
        await broker.QueuesAsync(TestContext.Current.CancellationToken);
        await broker.ExchangesAsync(TestContext.Current.CancellationToken);
        await broker.BindingsAsync(TestContext.Current.CancellationToken);
        await broker.PermissionsAsync(TestContext.Current.CancellationToken);
        await frontend.StartAsync(TestContext.Current.CancellationToken);
        await frontend.StartDeviceBuildAsync(TestContext.Current.CancellationToken);
        await new DeviceReads(runner, Paths, time).ReadAsync(TestContext.Current.CancellationToken);
        await new WorkstationDoctor(runner, compose, Paths, Options.Create(new AdminOptions()), time, _ => new InstallReading(InstallState.Current, "")).ReadAsync(TestContext.Current.CancellationToken);

        return [.. runner.Started.Select(s => (string[])[s.FileName, .. s.Arguments])];
    }

    /// <summary>
    /// A line's tokens as a shell hands them to the program: PowerShell's single quotes are the shell's, not the
    /// argument's. A line the host runs whole, through PowerShell, matches as one argument instead.
    /// </summary>
    private static string[] Tokens(string line) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Length > 1 && t[0] == '\'' && t[^1] == '\'' ? t[1..^1] : t)];

    /// <summary>
    /// A line of a script the host hands PowerShell whole, as its one <c>-Command</c> argument of one line or several: the line is
    /// run as the document has it. Whether a whole block is the document's is LoadRunDriftTests' to say.
    /// </summary>
    private static bool RunsWhole(string[] host, string line) =>
        host is ["powershell", "-NoProfile", "-Command", var script] && script.Split('\n').Any(l => l.Trim() == line);

    /// <summary>
    /// The host runs the document's line when every token of the line is in the host's argv and the
    /// tokens that are not service names appear in the same order. The host may add flags (a JSON
    /// formatter, <c>-T</c>, a tail); it may not drop or reorder a verb.
    /// </summary>
    private static bool Runs(string[] host, string[] documented, HashSet<string> services)
    {
        if (!documented.All(host.Contains))
        {
            return false;
        }

        int at = 0;

        foreach (string token in documented.Where(t => !services.Contains(t)))
        {
            at = Array.IndexOf(host, token, at);

            if (at < 0)
            {
                return false;
            }

            at++;
        }

        return true;
    }

    private static string[] FencedLines(string document)
    {
        List<string> lines = [];
        bool fenced = false;

        foreach (string raw in document.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced && line.Length > 0 && !line.StartsWith('#'))
            {
                lines.Add(line);
            }
        }

        return [.. lines];
    }

    /// <summary>Every configured URL, read off <see cref="AdminOptions"/> so a surface added there needs no line here.</summary>
    private static string[] Urls(AdminOptions o) =>
        [.. typeof(AdminOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Url", StringComparison.Ordinal))
            .Select(p => (string)p.GetValue(o)!)];

    [GeneratedRegex(@"^(docker|dotnet|npm|node|git|py|Get-NetTCPConnection|Get-Content|curl\.exe|&) ")]
    private static partial Regex Command();

    [GeneratedRegex(@"https?://[^\s""'/]+[^\s""']*")]
    private static partial Regex Url();

    /// <summary>One right-hand side of a value line: <c>$null</c>, a variable, a number, a quoted string, a <c>[guid]</c>, or a nested array or hashtable.</summary>
    private const string Value = @"(?:\$null|\$[\w.]+|-?\d+(?:\.\d+)?|'[^']*'|""[^""]*""|\[guid\]::NewGuid\(\)|@\(.*\)|@\{[^}]*\})";

    /// <summary>
    /// PowerShell that builds a value: a variable assigned a value, or the open of a hashtable, a member line inside one
    /// whose right-hand sides are each a literal, a variable, a <c>[guid]</c> or a nested hashtable or array, the close that
    /// pipes it to <c>ConvertTo-Json</c>, or an environment variable the next command reads. A control line (<c>} else</c>)
    /// builds nothing and is no value. The <c>ConnectionStrings__</c> environment lines are held to the host's own by
    /// ReplayDriftTests; any other is listed in <see cref="NotRun"/> with the section that sets it.
    /// </summary>
    [GeneratedRegex(@"^(?:\$\w+ = @\{|\$\w+ = " + Value + @"(?: \| ConvertTo-Json)?(?:; \$\w+ = " + Value + @")*|\$env:ConnectionStrings__\w+ = '[^']*'|\w+ = " + Value + @"(?:; \w+ = " + Value + @")*|\} \| ConvertTo-Json(?: -Depth \d+)?\))$")]
    private static partial Regex BuildsAValue();
}
