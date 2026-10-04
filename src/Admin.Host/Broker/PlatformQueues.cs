namespace Admin.Host.Broker;

/// <summary>One consumer queue the platform declares: its name, the endpoint that consumes it, and the backend constant that names it.</summary>
public sealed record PlatformQueue(string Name, string Consumer, string BackendSymbol);

/// <summary>
/// The platform's consumer queues, each copied from the blueprint-backend constant that names it (one per
/// service's Messaging/DependencyInjection.cs, and Catalog's StockLevelConsumer). The drift gate reads every
/// one of those constants against this table and fails on a queue in either list that the other lacks
/// (tests/Admin.Host.Tests/Drift/QueueDriftTests.cs).
/// </summary>
public static class PlatformQueues
{
    /// <summary>The queue run-locally.md says to let drain before ordering a product just published.</summary>
    public const string Projection = "ordering-catalog-events";

    public static readonly PlatformQueue[] Table =
    [
        new(Projection, "Ordering's price projection", "Ordering.Infrastructure.Messaging.DependencyInjection.CatalogEventsQueue"),
        new("ordering-commands", "Ordering's command consumers", "Ordering.Infrastructure.Messaging.DependencyInjection.CommandsQueue"),
        new("ordering-fulfilment-saga", "the fulfilment saga", "Ordering.Infrastructure.Messaging.DependencyInjection.FulfilmentSagaQueue"),
        new("ordering-stock-events", "Ordering's stock projection", "Ordering.Infrastructure.Messaging.DependencyInjection.StockEventsQueue"),
        new("inventory-commands", "Inventory's reserve and release", "Inventory.Infrastructure.Messaging.DependencyInjection.CommandsQueue"),
        new("inventory-events", "Inventory's order events", "Inventory.Infrastructure.Messaging.DependencyInjection.EventsQueue"),
        new("payments-commands", "Payments' authorise and void", "Payments.Infrastructure.Messaging.DependencyInjection.CommandsQueue"),
        new("payments-events", "Payments' order events", "Payments.Infrastructure.Messaging.DependencyInjection.EventsQueue"),
        new("catalog-inventory-events", "Catalog's stock levels", "Catalog.Infrastructure.Messaging.StockLevelConsumer.Queue"),
        new("shipping-events", "Shipping's despatch and cancel", "Shipping.Infrastructure.Messaging.DependencyInjection.EventsQueue"),
        new("bff-order-events", "the BFF's order projection", "Web.Bff.Messaging.DependencyInjection.EventsQueue"),
    ];

    /// <summary>What a queue feeds, its <c>_error</c> twin included, or null for a queue the table does not name.</summary>
    public static string? ConsumerOf(string queue)
    {
        string name = queue.EndsWith(BrokerService.ErrorSuffix, StringComparison.Ordinal) ? queue[..^BrokerService.ErrorSuffix.Length] : queue;

        return Table.FirstOrDefault(q => q.Name == name)?.Consumer;
    }
}
