using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Platform;

/// <summary>A gateway route that reaches a service: the method (null for any), the path prefix and the edge policy, as <see cref="GatewayRoutes.Routes"/> copies them.</summary>
public sealed record MapRoute(string? Method, string Prefix, string Policy);

/// <summary>An exchange bound to a queue, with the routing key it matches, as the broker's <c>list_bindings</c> reports it.</summary>
public sealed record MapBinding(string Source, string RoutingKey);

/// <summary>
/// A queue a service consumes. <see cref="Messages"/> is the depth the broker reports, null when the broker does not
/// list the queue; <see cref="Parked"/> is <c>error</c> or <c>skipped</c> for a parked twin of the service's queue.
/// </summary>
public sealed record MapQueue(string Name, string Consumer, long? Messages, string? Parked, IReadOnlyList<MapBinding> Bindings);

/// <summary>A service of <see cref="PlatformServices.All"/> with the routes that reach it and the queues it consumes.</summary>
public sealed record MapService(string Name, string? Url, IReadOnlyList<MapRoute> Routes, IReadOnlyList<MapQueue> Queues);

/// <summary>
/// The platform seen from the running side. <see cref="Disagreements"/> are the places where what the broker reports and
/// what the console's tables copy from the code differ; the map shows them and resolves none (spec §5.15).
/// </summary>
public sealed record PlatformMapView(bool BrokerReachable, string? Error, IReadOnlyList<MapService> Services, IReadOnlyList<string> Disagreements);

/// <summary>
/// Joins <see cref="PlatformServices.All"/>, <see cref="GatewayRoutes.Routes"/>, <see cref="PlatformQueues.Table"/> and the
/// broker's queue and binding listings (through <see cref="BrokerService"/>, never the management API) into one view, at
/// request time. It keeps no list of its own.
/// </summary>
public sealed class PlatformMapService(BrokerService broker, IOptions<AdminOptions> options)
{
    public async Task<PlatformMapView> ReadAsync(CancellationToken cancellationToken)
    {
        QueuesView queues = await broker.QueuesAsync(cancellationToken);
        BindingsView bindings = await broker.BindingsAsync(cancellationToken);
        List<string> disagreements = [];
        string? error = queues.Reachable ? (bindings.Reachable ? null : bindings.Error) : queues.Error;

        MapService[] services =
        [
            .. PlatformServices.All.Select(service => new MapService(
                service.Name,
                service.Url?.Invoke(options.Value),
                [.. GatewayRoutes.Routes
                    .Where(r => service.GatewayPrefix is not null && r.Prefix.StartsWith(service.GatewayPrefix, StringComparison.Ordinal))
                    .Select(r => new MapRoute(r.Method, r.Prefix, r.Policy))],
                [.. PlatformQueues.Table
                    .Where(q => q.BackendSymbol.StartsWith(service.BackendNamespace + ".", StringComparison.Ordinal))
                    .SelectMany(q => Twins(q, queues, bindings))])),
        ];

        if (queues.Reachable)
        {
            HashSet<string> known = [.. PlatformQueues.Table.Select(q => q.Name)];

            foreach (PlatformQueue row in PlatformQueues.Table.Where(q => queues.Queues.All(b => b.Name != q.Name)))
            {
                disagreements.Add($"{row.Name} is in the console's queue table but the broker does not list it.");
            }

            foreach (BrokerQueue queue in queues.Queues.Where(q => !known.Contains(ParkedQueues.EndpointOf(q.Name))))
            {
                disagreements.Add($"{queue.Name} is on the broker but in no service's queue table.");
            }
        }

        foreach ((string? method, string prefix, string policy) in GatewayRoutes.Routes)
        {
            if (!PlatformServices.All.Any(s => s.GatewayPrefix is not null && prefix.StartsWith(s.GatewayPrefix, StringComparison.Ordinal)))
            {
                disagreements.Add($"{method ?? "ANY"} {prefix} ({policy}) is a gateway route no service record names.");
            }
        }

        return new PlatformMapView(queues.Reachable && bindings.Reachable, error, services, disagreements);
    }

    private static IEnumerable<MapQueue> Twins(PlatformQueue row, QueuesView queues, BindingsView bindings) =>
        queues.Queues
            .Where(b => ParkedQueues.EndpointOf(b.Name) == row.Name)
            .Select(b => new MapQueue(
                b.Name,
                row.Consumer,
                b.Messages,
                b.Parked,
                [.. bindings.Bindings
                    .Where(x => x.Destination == b.Name && x.DestinationKind == "queue" && x.Source.Length > 0)
                    .Select(x => new MapBinding(x.Source, x.RoutingKey))]))
            .DefaultIfEmpty(new MapQueue(row.Name, row.Consumer, null, null, []));
}

public static class PlatformMapEndpoints
{
    public static IEndpointRouteBuilder MapPlatformMap(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/map", async (PlatformMapService map, CancellationToken cancellationToken) =>
            TypedResults.Ok(await map.ReadAsync(cancellationToken)));

        return app;
    }
}
