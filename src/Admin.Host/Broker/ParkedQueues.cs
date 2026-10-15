namespace Admin.Host.Broker;

/// <summary>A kind of queue MassTransit parks a message on: the suffix it adds, and the runbook that owns what to do about it.</summary>
public sealed record ParkedKind(string Name, string Suffix, string Runbook);

/// <summary>
/// The two kinds of parked queue, each suffix copied from <c>SUFFIXES</c> in blueprint-backend's
/// <c>tools/dead-letters/dead_letters.py</c> and each runbook a file under its <c>docs/runbooks/</c>; the drift gate
/// reads both (tests/Admin.Host.Tests/Drift/QueueDriftTests.cs). The console shows which kind a queue is and
/// where its runbook is, and decides nothing.
/// </summary>
public static class ParkedQueues
{
    public static readonly ParkedKind Error = new("error", BrokerService.ErrorSuffix, "error-queue.md");

    public static readonly ParkedKind Skipped = new("skipped", "_skipped", "skipped-queue.md");

    public static readonly ParkedKind[] All = [Error, Skipped];

    /// <summary>Which kind of parked queue this is, or null for a queue that is not one.</summary>
    public static ParkedKind? KindOf(string queue) => All.FirstOrDefault(k => queue.EndsWith(k.Suffix, StringComparison.Ordinal));

    /// <summary>The endpoint a queue belongs to: a parked queue's name without its suffix, any other queue's name as it is.</summary>
    public static string EndpointOf(string queue) => KindOf(queue) is { } kind ? queue[..^kind.Suffix.Length] : queue;
}
