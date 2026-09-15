namespace Admin.Host.Broker;

/// <summary>A queue and its depth. <see cref="Messages"/> is ready plus unacknowledged, as rabbitmqctl reports it.</summary>
public sealed record BrokerQueue(string Name, long? Messages, bool IsErrorQueue);

/// <summary>Whether a queue has caught up: declared and empty. A queue that is not declared is not drained.</summary>
public sealed record ProjectionDrain(string Queue, bool Found, long? Messages, bool Drained);

/// <summary>The queue listing. A broker that does not answer is a state, not an exception (spec §9).</summary>
public sealed record QueuesView(bool Reachable, string? Error, IReadOnlyList<BrokerQueue> Queues, ProjectionDrain Projection);

public sealed record BrokerExchange(string Name, string Type);

public sealed record ExchangesView(bool Reachable, string? Error, IReadOnlyList<BrokerExchange> Exchanges);

public sealed record BrokerPermission(string User, string Configure, string Write, string Read);

public sealed record PermissionsView(bool Reachable, string? Error, IReadOnlyList<BrokerPermission> Permissions);
