namespace Admin.Host.Broker;

public static class BrokerEndpoints
{
    public static IEndpointRouteBuilder MapBroker(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/broker/queues", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.QueuesAsync(cancellationToken)));

        app.MapGet("/api/broker/exchanges", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.ExchangesAsync(cancellationToken)));

        app.MapGet("/api/broker/permissions", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.PermissionsAsync(cancellationToken)));

        return app;
    }
}
