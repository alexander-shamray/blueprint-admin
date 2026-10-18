namespace Admin.Host.Broker;

/// <summary>
/// A queue and its depth. <see cref="Messages"/> is ready plus unacknowledged, as rabbitmqctl reports it;
/// <see cref="Consumer"/> is what <see cref="PlatformQueues.Table"/> says consumes it, null for a queue it does not name.
/// <see cref="Parked"/> is the <see cref="ParkedKind.Name"/> of a parked queue and <see cref="Runbook"/> the URL of the
/// runbook that owns it; both are null for a queue that is not one.
/// </summary>
public sealed record BrokerQueue(string Name, long? Messages, string? Consumer = null, string? Parked = null, string? Runbook = null);

/// <summary>Whether a queue has caught up: declared and empty. A queue that is not declared is not drained.</summary>
public sealed record ProjectionDrain(string Queue, bool Found, long? Messages, bool Drained);

/// <summary>The queue listing. A broker that does not answer is a state, not an exception (spec §9).</summary>
public sealed record QueuesView(bool Reachable, string? Error, IReadOnlyList<BrokerQueue> Queues, ProjectionDrain Projection);

public sealed record BrokerExchange(string Name, string Type);

public sealed record ExchangesView(bool Reachable, string? Error, IReadOnlyList<BrokerExchange> Exchanges);

/// <summary>
/// One binding as run-locally.md's <c>list_bindings</c> read gives it: an exchange (<see cref="Source"/>, empty for the
/// broker's default exchange) bound to a queue or another exchange (<see cref="DestinationKind"/> says which), with the
/// routing key it matches.
/// </summary>
public sealed record BrokerBinding(string Source, string SourceKind, string Destination, string DestinationKind, string RoutingKey);

public sealed record BindingsView(bool Reachable, string? Error, IReadOnlyList<BrokerBinding> Bindings);

public sealed record BrokerPermission(string User, string Configure, string Write, string Read);

public sealed record PermissionsView(bool Reachable, string? Error, IReadOnlyList<BrokerPermission> Permissions);
