using Admin.Host.Broker;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Privacy;

/// <summary>A holder as the Erasure screen lists it: its name and the depth of its erasure queue, null when the broker did not say.</summary>
public sealed record HolderView(string Name, string Queue, long? Pending);

/// <summary>
/// The holders an erasure request waits on. A broker that does not answer is a state, not an exception (spec §9):
/// the list is still there, with no depths. The request itself is read and raised through the proxy, as the gateway
/// answers it, so this carries only what the gateway does not.
/// </summary>
public sealed record HoldersView(bool Reachable, string? Error, IReadOnlyList<HolderView> Holders, long? CompletionsPending, string RunbookUrl);

public static class ErasureEndpoints
{
    public static IEndpointRouteBuilder MapErasure(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/erasure/holders", async (BrokerService broker, IOptions<AdminOptions> options, CancellationToken cancellationToken) =>
        {
            QueuesView queues = await broker.QueuesAsync(cancellationToken);
            long? Depth(string name) => queues.Reachable ? queues.Queues.FirstOrDefault(q => q.Name == name)?.Messages : null;

            return TypedResults.Ok(new HoldersView(
                queues.Reachable,
                queues.Error,
                [.. ErasureHolders.Table.Select(h => new HolderView(h.Name, h.Queue, Depth(h.Queue)))],
                Depth(ErasureHolders.CompletionsQueue),
                $"{options.Value.BackendRunbooksUrl.TrimEnd('/')}/{ErasureHolders.OverdueRunbook}"));
        });

        return app;
    }
}
