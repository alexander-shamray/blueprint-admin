using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// What the BFF projection rebuild copies from blueprint-backend and run-locally.md, each held to its owner: the tool's
/// environment keys and publishers, the databases, the sa password and the BFF's broker account Compose gives, the
/// loopback addresses Compose publishes them on, the words of the tool's README the screen repeats before it runs, and the
/// six <c>$env:</c> lines of the document against the connections the host hands the tool.
/// </summary>
public sealed partial class ReplayDriftTests
{
    private static readonly AdminOptions Defaults = new();

    [Fact]
    public void The_environment_keys_and_the_publishers_are_the_tools()
    {
        string settings = Backend.Read("tools", "bff-replay", "ReplaySettings.cs");
        string publisher = Backend.Read("tools", "bff-replay", "Publisher.cs");

        Constant(settings, "BffKey").ShouldBe(BffReplayConnections.BffKey);
        Constant(settings, "BrokerKey").ShouldBe(BffReplayConnections.BrokerKey);
        publisher.ShouldContain("$\"ConnectionStrings__{Name}Outbox\"", Case.Sensitive, "the tool no longer derives a publisher's key from its name");
        PublisherName().Matches(publisher).Select(m => m.Groups[1].Value).ShouldBe(BffReplayConnections.Publishers);
    }

    [Fact]
    public void Each_database_is_the_one_its_service_connects_to()
    {
        string units = AllUnits();

        string[] databases = ["Bff", .. BffReplayConnections.Publishers];

        foreach (string database in databases)
        {
            Regex.IsMatch(units, $@"ConnectionStrings__{database}: ""[^""]*Database={database};").ShouldBeTrue(database);
        }
    }

    [Fact]
    public void The_sa_password_and_the_published_addresses_are_the_models()
    {
        string infrastructure = Backend.Read("deploy", "compose", "infrastructure.yml");

        infrastructure.ShouldContain($"MSSQL_SA_PASSWORD: \"${{SQL_PASSWORD:-{Defaults.SqlPassword}}}\"", Case.Sensitive);
        infrastructure.ShouldContain("\"127.0.0.1:1433:1433\"", Case.Sensitive);
        infrastructure.ShouldContain("\"127.0.0.1:5672:5672\"", Case.Sensitive);
        BffReplayConnections.SqlServer.ShouldBe("127.0.0.1,1433");
    }

    [Fact]
    public void The_broker_account_is_the_bffs_own_and_the_service_it_stops_exists()
    {
        string bff = Backend.Read("deploy", "compose", "services", "web-bff.yml");

        bff.ShouldContain($"ConnectionStrings__RabbitMq: \"{Defaults.BffBrokerConnection.Replace("127.0.0.1", "rabbitmq", StringComparison.Ordinal)}\"", Case.Sensitive);
        Compose.Services().Select(s => s.Name).ShouldContain(Admin.Host.Broker.ProjectionRebuildService.BffService);
    }

    [Fact]
    public void Every_limit_the_screen_states_is_still_in_the_tools_readme()
    {
        string readme = Markup().Replace(Backend.Read("tools", "bff-replay", "README.md"), "");
        readme = Whitespace().Replace(readme, " ");

        foreach (ReplayLimit limit in ProjectionRebuildService.Limits)
        {
            readme.ShouldContain(limit.Anchor, Case.Sensitive, limit.Text);
        }
    }

    [Fact]
    public void The_document_sets_the_six_connections_the_host_hands_the_tool()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        Dictionary<string, string> documented = EnvironmentLine().Matches(document).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        documented.ShouldBe(BffReplayConnections.For(Defaults).ToDictionary(), ignoreOrder: true);
    }

    private static string AllUnits() =>
        string.Join('\n', Directory.EnumerateFiles(Path.Combine(Backend.Dir, "deploy", "compose", "services"), "*.yml").Select(File.ReadAllText));

    private static string Constant(string source, string name) =>
        Regex.Match(source, $@"const string {name} = ""([^""]+)"";").Groups[1].Value;

    [GeneratedRegex(@"new\(""(\w+)"", ""\w+""\)")]
    private static partial Regex PublisherName();

    /// <summary>Backticks and bold markers, which the README sets around the words the anchors quote.</summary>
    [GeneratedRegex(@"[`*]")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\$env:(\w+) = '(.*)'\s*$", RegexOptions.Multiline)]
    private static partial Regex EnvironmentLine();
}
