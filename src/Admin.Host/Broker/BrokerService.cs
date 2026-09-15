using System.Text.Json;
using Admin.Host.Compose;

namespace Admin.Host.Broker;

/// <summary>
/// The three broker inspections run-locally.md gives, through rabbitmqctl inside the Compose rabbitmq
/// container (spec §5.5): the management UI on 15672 has no login, so the console does not use it.
/// </summary>
public sealed class BrokerService(ComposeService compose)
{
    /// <summary>The Compose service, owned by the backend's deploy/compose/infrastructure.yml.</summary>
    public const string Service = "rabbitmq";

    /// <summary>
    /// Ordering's price projection endpoint, the backend's
    /// Ordering.Infrastructure.Messaging.DependencyInjection.CatalogEventsQueue. run-locally.md says to
    /// wait for it to drain before ordering a product just published.
    /// </summary>
    public const string ProjectionQueue = "ordering-catalog-events";

    /// <summary>run-locally.md: error queues are named <c>&lt;endpoint&gt;_error</c>.</summary>
    public const string ErrorSuffix = "_error";

    public async Task<QueuesView> QueuesAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<QueueRow>? rows, string? error) = await ListAsync<QueueRow>(["list_queues", "name", "messages"], cancellationToken);

        if (rows is null)
        {
            return new QueuesView(false, error, [], new ProjectionDrain(ProjectionQueue, false, null, false));
        }

        List<BrokerQueue> queues = [.. rows
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new BrokerQueue(r.Name, r.Messages, r.Name.EndsWith(ErrorSuffix, StringComparison.Ordinal)))];

        return new QueuesView(true, null, queues, IsDrained(queues, ProjectionQueue));
    }

    public async Task<ExchangesView> ExchangesAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<BrokerExchange>? rows, string? error) = await ListAsync<BrokerExchange>(["list_exchanges", "name", "type"], cancellationToken);

        return rows is null
            ? new ExchangesView(false, error, [])
            : new ExchangesView(true, null, [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal)]);
    }

    public async Task<PermissionsView> PermissionsAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<BrokerPermission>? rows, string? error) = await ListAsync<BrokerPermission>(["list_permissions"], cancellationToken);

        return rows is null
            ? new PermissionsView(false, error, [])
            : new PermissionsView(true, null, [.. rows.OrderBy(r => r.User, StringComparer.Ordinal)]);
    }

    public static ProjectionDrain IsDrained(IReadOnlyList<BrokerQueue> queues, string queue)
    {
        BrokerQueue? found = queues.FirstOrDefault(q => q.Name == queue);

        return new ProjectionDrain(queue, found is not null, found?.Messages, found?.Messages == 0);
    }

    private async Task<(IReadOnlyList<T>? Rows, string? Error)> ListAsync<T>(string[] command, CancellationToken cancellationToken)
    {
        CommandOutput output = await compose.ExecAsync(Service, ["rabbitmqctl", .. command, "--formatter", "json"], cancellationToken);

        if (output.Error is not null)
        {
            return (null, output.Error);
        }

        try
        {
            return (RabbitCtlJson.Parse<T>(output.Stdout), null);
        }
        catch (JsonException ex)
        {
            return (null, $"rabbitmqctl {command[0]} output could not be parsed: {ex.Message}");
        }
    }

    private sealed record QueueRow(string Name, long? Messages);
}
