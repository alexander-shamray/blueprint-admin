using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.Extensions.Options;

namespace Admin.Host.Stack;

/// <summary>
/// blueprint-backend's §13.7 SLO run, <c>deploy/observability/slo/slo.js</c>, as one job on the Stack screen, so the golden-signal
/// strip beside it is read under load (spec §5.3). Every step is a block of run-locally.md's "Load run (k6)", run as written through
/// PowerShell from the backend clone and in the document's order: k6 answers, Prometheus answers through Grafana, the <c>slo-run</c>
/// client exists with its two roles, its token carries them, a product is chosen and restocked, then k6 runs. The console runs the
/// backend's script and carries none, so a change to the run there needs nothing here. A step that fails ends the job there, and
/// the run's own exit is k6's, read against <see cref="Verdicts"/>. The load-run job owns no process and carries its steps' output,
/// in order, under one id, as the reset does. It claims no <see cref="Compose.ComposeGate"/>: it stops and starts nothing, and a Down
/// under it fails the step it lands in.
/// </summary>
public sealed class LoadRunService(IProcessRunner runner, JobRegistry registry, RepoPaths paths, IOptions<AdminOptions> options)
{
    /// <summary>The load-run job's name in the job list, followed by its steps' names.</summary>
    public const string Kind = "load-run";

    /// <summary>Owner: run-locally.md step 0, the k6 read. It installs nothing and exits 0 either way, so its line says.</summary>
    internal const string K6Read =
        """if (Get-Command k6 -ErrorAction SilentlyContinue) { k6 version } else { 'k6: not installed (winget install k6 --source winget)' }""";

    /// <summary>Owner: run-locally.md step 0, the start of the line <see cref="K6Read"/> prints when k6 is missing.</summary>
    internal const string K6Missing = "k6: not installed";

    /// <summary>Owner: run-locally.md, "Load run (k6)", Prometheus read through Grafana. It exits 0 either way, so its line says.</summary>
    internal const string PrometheusReady =
        """try { Invoke-WebRequest http://localhost:3000/api/datasources/proxy/uid/prometheus/-/ready -UseBasicParsing | Out-Null; 'prometheus ready: yes' } catch { "prometheus ready: no ($($_.Exception.Message))" }""";

    /// <summary>Owner: run-locally.md, "Load run (k6)", the line <see cref="PrometheusReady"/> prints when Prometheus answers.</summary>
    internal const string PrometheusYes = "prometheus ready: yes";

    /// <summary>
    /// Owner: run-locally.md, "Load run (k6)", the <c>slo-run</c> client made when it is missing and granted its two roles either way.
    /// The document's read of whether it exists is not run: this block reads the same and says <c>created</c> or <c>already there</c>.
    /// </summary>
    internal static readonly string SloClient = Block(
        """
        $ErrorActionPreference = 'Stop'
        $admin = (Invoke-RestMethod http://localhost:8080/realms/master/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
        $h = @{ Authorization = "Bearer $admin" }; $realm = 'http://localhost:8080/admin/realms/commerce'
        $c = @((Invoke-RestMethod "$realm/clients?clientId=slo-run" -Headers $h) | Where-Object clientId -eq 'slo-run')
        if ($c.Count -eq 0) {
          Invoke-RestMethod "$realm/clients" -Method Post -Headers $h -ContentType 'application/json' -Body (@{ clientId = 'slo-run'; enabled = $true; publicClient = $false; serviceAccountsEnabled = $true; standardFlowEnabled = $false; directAccessGrantsEnabled = $false; defaultClientScopes = @('commerce-api') } | ConvertTo-Json) | Out-Null
          $c = @((Invoke-RestMethod "$realm/clients?clientId=slo-run" -Headers $h) | Where-Object clientId -eq 'slo-run'); 'slo-run: created'
        } else { 'slo-run: already there' }
        $sa = Invoke-RestMethod "$realm/clients/$($c[0].id)/service-account-user" -Headers $h
        $api = @((Invoke-RestMethod "$realm/clients?clientId=commerce-api" -Headers $h) | Where-Object clientId -eq 'commerce-api')[0]
        $roles = 'orders:write', 'inventory:admin' | ForEach-Object { $r = Invoke-RestMethod "$realm/clients/$($api.id)/roles/$_" -Headers $h; @{ id = $r.id; name = $r.name } } | ConvertTo-Json
        Invoke-RestMethod "$realm/users/$($sa.id)/role-mappings/clients/$($api.id)" -Method Post -Headers $h -ContentType 'application/json' -Body $roles | Out-Null
        'slo-run roles: ' + (((Invoke-RestMethod "$realm/users/$($sa.id)/role-mappings/clients/$($api.id)" -Headers $h) | ForEach-Object { $_.name } | Sort-Object) -join ',')
        """);

    /// <summary>
    /// Owner: run-locally.md, "Load run (k6)", the token check, which exits 1 on a fail. The document's secret read is not run: it
    /// prints the secret, and the run block reads it into k6's environment itself.
    /// </summary>
    internal static readonly string TokenCheck = Block(
        """
        $ErrorActionPreference = 'Stop'
        $adm = (Invoke-RestMethod http://localhost:8080/realms/master/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
        $h = @{ Authorization = "Bearer $adm" }; $c = @((Invoke-RestMethod 'http://localhost:8080/admin/realms/commerce/clients?clientId=slo-run' -Headers $h) | Where-Object clientId -eq 'slo-run')[0]
        $secret = (Invoke-RestMethod "http://localhost:8080/admin/realms/commerce/clients/$($c.id)/client-secret" -Headers $h).value
        $jwt = (Invoke-RestMethod http://localhost:8080/realms/commerce/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'client_credentials'; client_id = 'slo-run'; client_secret = $secret }).access_token
        $p = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/'); $p += '=' * ((4 - $p.Length % 4) % 4)
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
        $perm = @($claims.permission); $g = [guid]::Empty
        $ok = ($perm -contains 'orders:write') -and ($perm -contains 'inventory:admin') -and [guid]::TryParse($claims.sub, [ref]$g) -and $claims.iss -eq 'http://localhost:8080/realms/commerce'
        "permission: $($perm -join ',')"; "sub: $($claims.sub)"; "iss: $($claims.iss)"
        'token check: ' + $(if ($ok) { 'pass' } else { 'fail' })
        if (-not $ok) { exit 1 }
        """);

    /// <summary>Owner: run-locally.md, "Load run (k6)", the product and currency read, which exits 1 when none is usable.</summary>
    internal static readonly string Product = Block(
        """
        $row = docker compose -f deploy/compose/docker-compose.yml exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -h -1 -W -s ',' -Q "SET NOCOUNT ON; SELECT TOP 1 LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices WHERE IsAvailable = 1 AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY CASE WHEN CONVERT(char(36), ProductId) LIKE '5EED0000-%' THEN 0 ELSE 1 END, ProductId"
        $id, $cur = "$row".Trim() -split ','
        if (-not $cur) { 'product: none usable in ordering.ProductPrices'; exit 1 }
        '--- product ---'; $id; '--- currency ---'; $cur
        """);

    /// <summary>Owner: run-locally.md, "Load run (k6)", the restock of that product to an absolute count.</summary>
    internal static readonly string Restock = Block(
        """
        $ErrorActionPreference = 'Stop'
        $adm = (Invoke-RestMethod http://localhost:8080/realms/master/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
        $h = @{ Authorization = "Bearer $adm" }; $c = @((Invoke-RestMethod 'http://localhost:8080/admin/realms/commerce/clients?clientId=slo-run' -Headers $h) | Where-Object clientId -eq 'slo-run')[0]
        $secret = (Invoke-RestMethod "http://localhost:8080/admin/realms/commerce/clients/$($c.id)/client-secret" -Headers $h).value
        $jwt = (Invoke-RestMethod http://localhost:8080/realms/commerce/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'client_credentials'; client_id = 'slo-run'; client_secret = $secret }).access_token
        $id = ("$(docker compose -f deploy/compose/docker-compose.yml exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -h -1 -W -s ',' -Q "SET NOCOUNT ON; SELECT TOP 1 LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices WHERE IsAvailable = 1 AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY CASE WHEN CONVERT(char(36), ProductId) LIKE '5EED0000-%' THEN 0 ELSE 1 END, ProductId")".Trim() -split ',')[0]
        Invoke-RestMethod "http://localhost:5000/api/v1/inventory/stock/$id" -Method Put -Headers @{ Authorization = "Bearer $jwt" } -ContentType 'application/json' -Body (@{ onHand = 100000 } | ConvertTo-Json) | Out-Null
        $s = Invoke-RestMethod "http://localhost:5000/api/v1/inventory/stock/$id" -Headers @{ Authorization = "Bearer $jwt" }
        "product: $id"; "available: $($s.available)"; "reserved: $($s.reserved)"
        """);

    /// <summary>Owner: run-locally.md, "Load run (k6)", the run, which exits with k6's own code.</summary>
    internal static readonly string Run = Block(
        """
        $ErrorActionPreference = 'Stop'
        $adm = (Invoke-RestMethod http://localhost:8080/realms/master/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token
        $h = @{ Authorization = "Bearer $adm" }; $c = @((Invoke-RestMethod 'http://localhost:8080/admin/realms/commerce/clients?clientId=slo-run' -Headers $h) | Where-Object clientId -eq 'slo-run')[0]
        $env:CLIENT_SECRET = (Invoke-RestMethod "http://localhost:8080/admin/realms/commerce/clients/$($c.id)/client-secret" -Headers $h).value
        $id, $cur = "$(docker compose -f deploy/compose/docker-compose.yml exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -h -1 -W -s ',' -Q "SET NOCOUNT ON; SELECT TOP 1 LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices WHERE IsAvailable = 1 AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY CASE WHEN CONVERT(char(36), ProductId) LIKE '5EED0000-%' THEN 0 ELSE 1 END, ProductId")".Trim() -split ','
        $ErrorActionPreference = 'Continue'
        k6 run --no-color -e BASE_URL=http://localhost:5000 -e PROM_URL=http://localhost:3000/api/datasources/proxy/uid/prometheus -e TOKEN_URL=http://localhost:8080/realms/commerce/protocol/openid-connect/token -e CLIENT_ID=slo-run -e "SLO_PRODUCT_ID=$id" -e "SLO_CURRENCY=$cur" deploy/observability/slo/slo.js
        $code = $LASTEXITCODE; "k6 exit: $code"; exit $code
        """);

    /// <summary>
    /// Every step in the document's order: its name, its block, and what its output must say beyond an exit of 0, for the two
    /// blocks that exit 0 whatever they found.
    /// </summary>
    public static readonly IReadOnlyList<LoadRunStep> Steps =
    [
        new("k6", K6Read, lines => !lines.Any(l => l.StartsWith(K6Missing, StringComparison.Ordinal))),
        new("prometheus", PrometheusReady, lines => lines.Contains(PrometheusYes)),
        new("slo-run", SloClient, _ => true),
        new("token", TokenCheck, _ => true),
        new("product", Product, _ => true),
        new("restock", Restock, _ => true),
        new("run", Run, _ => true),
    ];

    /// <summary>Owner: run-locally.md, "Load run (k6)", what k6's exit code says. Any other code is k6's own, and not read.</summary>
    public static readonly IReadOnlyDictionary<int, string> Verdicts = new Dictionary<int, string>
    {
        [0] = "a pass: every threshold held, and teardown() found no breached row",
        [99] = "a fail: a k6 threshold was crossed, and its line under THRESHOLDS is marked ✗",
        [107] = "a fail: setup() or teardown() called fail(), and the level=error line says why",
        [105] = "interrupted, so neither a pass nor a fail",
    };

    /// <summary>
    /// What a Stop in the run block leaves, said on the job. The block reads the secret and the product before its k6 line, so
    /// k6 may not have started; if it had, the console ended its process tree, which run-locally.md says skips teardown(), and the
    /// orders already placed go on through the saga.
    /// </summary>
    internal const string StoppedInRun =
        "stopped in the run block: if k6 had started, teardown() did not run and there is no verdict; any orders it placed stay, as a finished run's do.";

    /// <summary>What a Stop before k6 leaves: no load was driven, so there is no verdict to lose and no order placed.</summary>
    internal static string StoppedBefore(string step) => $"stopped at the {step} step, before k6 started: no load was driven and no order was placed.";

    private readonly Lock gate = new();

    // Written under the gate; Current reads it without, and a reference read is atomic.
    private volatile Job? current;
    private Job? step;
    private bool stopping;

    /// <summary>The last load run, running or exited, for the Stack screen; null before the first.</summary>
    public Job? Current => current;

    /// <summary>
    /// Starts a run, or returns the one still going: two at once would race each other's restock and split the rate the run's
    /// thresholds were set for.
    /// </summary>
    public Job Start()
    {
        lock (gate)
        {
            if (current is { State: JobState.Running } running)
            {
                return running;
            }

            Job run = registry.Create(new ProcessSpec(Kind, [.. Steps.Select(s => s.Name)], paths.BackendDir));
            current = run;
            step = null;
            stopping = false;
            _ = Task.Run(() => RunAsync(run));

            return run;
        }
    }

    /// <summary>
    /// Ends the run going, at the step it is in, and waits for the job to say so; null when none is going. The step's process
    /// tree is killed (spec §5.2), which is not k6's Ctrl+C. A Stop that arrives after k6 has ended by itself kills nothing and
    /// leaves k6's verdict to stand.
    /// </summary>
    public async Task<Job?> StopAsync()
    {
        Job run;
        Job? running;

        lock (gate)
        {
            if (current is not { State: JobState.Running } going)
            {
                return null;
            }

            run = going;
            running = step;

            // k6 already ended on its own: its verdict stands, and the job is about to say it.
            if (running is { State: JobState.Exited } && IsRun(running))
            {
                running = null;
            }
            else
            {
                stopping = true;
            }
        }

        if (running is not null)
        {
            // Not the request's token: an aborted request must not leave k6 half-killed.
            await runner.StopAsync(running, CancellationToken.None);
        }

        await run.Completion;

        return run;
    }

    /// <summary>One block run whole, from the backend clone, as run-locally.md's PowerShell blocks are.</summary>
    public static ProcessSpec Spec(string block, RepoPaths paths, string? sqlPassword = null) =>
        new("powershell", Arguments(sqlPassword is null ? block : block.Replace(RunLocallySqlPassword, sqlPassword.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)), paths.BackendDir);

    /// <summary>
    /// The <c>sa</c> password run-locally.md's blocks carry, which <see cref="AdminOptions.SqlPassword"/> defaults to. The
    /// blocks stay verbatim, and a stack started with another <c>SQL_PASSWORD</c> has this literal swapped for the configured one
    /// when a step starts, a single quote in it doubled as PowerShell quotes it. The job's command line is not scrubbed: a secret is scrubbed as a word, and this one sits inside the block.
    /// </summary>
    internal const string RunLocallySqlPassword = "Local_Dev_Pa55w0rd!";

    public static string[] Arguments(string block) => ["-NoProfile", "-Command", block];

    private async Task RunAsync(Job run)
    {
        try
        {
            foreach (LoadRunStep next in Steps)
            {
                Job job;

                lock (gate)
                {
                    if (stopping)
                    {
                        run.Append(OutputStream.Stderr, StoppedBefore(next.Name));
                        run.MarkExited(-1);
                        return;
                    }

                    job = runner.Start(Spec(next.Block, paths, options.Value.SqlPassword));
                    step = job;
                }

                (int exitCode, List<string> stdout) = await FollowAsync(run, next, job);
                bool stopped;

                lock (gate)
                {
                    stopped = stopping;
                }

                if (stopped)
                {
                    run.Append(OutputStream.Stderr, IsRun(job) ? StoppedInRun : StoppedBefore(next.Name));
                    run.MarkExited(-1);
                    return;
                }

                if (ReferenceEquals(next, Steps[^1]))
                {
                    run.Append(exitCode == 0 ? OutputStream.Stdout : OutputStream.Stderr, $"k6 exited {exitCode}: {Verdict(exitCode)}");
                    run.MarkExited(exitCode);
                    return;
                }

                if (exitCode != 0 || !next.Passed(stdout))
                {
                    string said = exitCode != 0 ? $"exited with {exitCode}" : $"said \"{stdout.LastOrDefault(l => l.Length > 0) ?? ""}\"";
                    run.Append(OutputStream.Stderr, $"{next.Name} {said}; the load run stops here.");
                    run.MarkExited(exitCode != 0 ? exitCode : 1);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            run.Append(OutputStream.Stderr, $"load run failed: {ex.Message}");
            run.MarkExited(-1);
        }
    }

    /// <summary>Copies a step's lines into the run as they arrive, under a line naming the step and its own job.</summary>
    private static async Task<(int ExitCode, List<string> Stdout)> FollowAsync(Job run, LoadRunStep next, Job job)
    {
        List<string> stdout = [];
        run.Append(OutputStream.Stdout, $"== {next.Name} (job {job.Id})");

        await foreach (OutputLine line in job.Follow(-1, CancellationToken.None))
        {
            run.Append(line.Stream, line.Text);

            if (line.Stream == OutputStream.Stdout)
            {
                stdout.Add(line.Text.Trim());
            }
        }

        return (await job.Completion, stdout);
    }

    /// <summary>Whether a step's job is k6's own, the last block, as it was started: with the configured <c>sa</c> password swapped in.</summary>
    private bool IsRun(Job job) => job.Spec.Arguments is [.., var block] && block == Spec(Run, paths, options.Value.SqlPassword).Arguments[^1];

    private static string Verdict(int exitCode) => Verdicts.TryGetValue(exitCode, out string? verdict) ? verdict : "k6's own code, which run-locally.md does not read";

    /// <summary>A block as the document prints it, one line to a newline, whatever line ending this file was checked out with.</summary>
    private static string Block(string text) => text.ReplaceLineEndings("\n");
}

/// <summary>One block of the load run: its name on the job, the PowerShell it runs, and what its output must say to go on.</summary>
public sealed record LoadRunStep(string Name, string Block, Func<IReadOnlyList<string>, bool> Passed);
