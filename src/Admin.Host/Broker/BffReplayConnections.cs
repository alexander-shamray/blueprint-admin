using Admin.Host.Config;

namespace Admin.Host.Broker;

/// <summary>
/// The six connections <c>tools/bff-replay</c> reads from its environment, as run-locally.md sets them against the
/// local Compose stack. The key names are copied from the tool (<c>ReplaySettings</c> and <c>Publisher</c> in
/// blueprint-backend), the databases and the BFF's broker account from <c>deploy/compose/services/</c>, and the
/// drift gate reads all of them (tests/Admin.Host.Tests/Drift/ReplayDriftTests.cs).
/// </summary>
public static class BffReplayConnections
{
    public const string BffKey = "ConnectionStrings__Bff";

    public const string BrokerKey = "ConnectionStrings__RabbitMq";

    /// <summary>
    /// SQL Server as Compose publishes it, on <c>127.0.0.1</c> alone (infrastructure.yml). The address is literal because
    /// <c>localhost</c> may resolve to <c>::1</c> first, where nothing listens: the tool timed out on it, measured live.
    /// </summary>
    public const string SqlServer = "127.0.0.1,1433";

    /// <summary>The four publishers whose outboxes the tool reads, each with the database its service owns.</summary>
    public static readonly string[] Publishers = ["Catalog", "Ordering", "Payments", "Shipping"];

    public static string OutboxKey(string publisher) => $"ConnectionStrings__{publisher}Outbox";

    public static IReadOnlyDictionary<string, string> For(AdminOptions options)
    {
        Dictionary<string, string> connections = new(StringComparer.Ordinal)
        {
            [BffKey] = Sql(options, "Bff"),
            [BrokerKey] = options.BffBrokerConnection,
        };

        foreach (string publisher in Publishers)
        {
            connections[OutboxKey(publisher)] = Sql(options, publisher);
        }

        return connections;
    }

    private static string Sql(AdminOptions options, string database) =>
        $"Server={SqlServer};Database={database};User Id=sa;Password={options.SqlPassword};TrustServerCertificate=True";
}
