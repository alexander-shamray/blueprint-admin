using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
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

        foreach (string line in lines.Where(l => !NotRun.ContainsKey(l)))
        {
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

        NotRun.Keys.Where(c => !lines.Contains(c)).ShouldBeEmpty();
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
    /// Compose service, the widest selection the Logs screen offers.
    /// </summary>
    private async Task<IReadOnlyList<string[]>> HostCommandsAsync()
    {
        ComposeService compose = new(runner, Paths, time);
        BrokerService broker = new(compose);
        using FrontendSupervisor frontend = new(runner, Paths, _ => true);

        compose.Up();
        compose.Down(wipeVolumes: false);
        compose.Down(wipeVolumes: true);
        compose.FollowLogs([.. Compose.Services().Select(s => s.Name)]);
        await compose.PsAsync(TestContext.Current.CancellationToken);
        await broker.QueuesAsync(TestContext.Current.CancellationToken);
        await broker.ExchangesAsync(TestContext.Current.CancellationToken);
        await broker.PermissionsAsync(TestContext.Current.CancellationToken);
        await frontend.StartAsync(TestContext.Current.CancellationToken);
        await new WorkstationDoctor(runner, compose, Paths, Options.Create(new AdminOptions()), time).ReadAsync(TestContext.Current.CancellationToken);

        return [.. runner.Started.Select(s => (string[])[s.FileName, .. s.Arguments])];
    }

    /// <summary>
    /// A line's tokens as a shell hands them to the program: PowerShell's single quotes are the shell's, not the
    /// argument's. A line the host runs whole, through PowerShell, matches as one argument instead.
    /// </summary>
    private static string[] Tokens(string line) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Length > 1 && t[0] == '\'' && t[^1] == '\'' ? t[1..^1] : t)];

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

    [GeneratedRegex(@"^(docker|npm|node|git|Get-NetTCPConnection|Get-Content) ")]
    private static partial Regex Command();

    [GeneratedRegex(@"https?://[^\s""'/]+[^\s""']*")]
    private static partial Regex Url();

    /// <summary>PowerShell that builds a value: a hashtable assignment, a member line inside one, or its close.</summary>
    [GeneratedRegex(@"^(\$\w+ = @\{.*|\w+ = .+|\}.*|\).*)$")]
    private static partial Regex BuildsAValue();
}
