using Admin.Host.Broker;

namespace Admin.Host.Privacy;

/// <summary>One personal-data holder Privacy asks to erase a subject, and the queue it consumes the request from.</summary>
public sealed record ErasureHolder(string Name, string Queue);

/// <summary>
/// The holders of an erasure request, copied from blueprint-backend's <c>deploy/compose/services/privacy.yml</c>
/// (<c>Privacy__Responders__n</c>, ADR-092), each with its <c>*-privacy</c> queue from <see cref="PlatformQueues.Table"/>.
/// The drift gate reads the Compose file against this table, both ways
/// (tests/Admin.Host.Tests/Drift/ErasureHoldersDriftTests.cs), so a holder added there fails the build here.
/// </summary>
public static class ErasureHolders
{
    /// <summary>The queue every holder answers on, whose depth is how many answers Privacy has yet to read.</summary>
    public const string CompletionsQueue = "privacy-completions";

    /// <summary>The runbook an overdue request is worked from, under <c>AdminOptions.BackendRunbooksUrl</c>.</summary>
    public const string OverdueRunbook = "erasure-overdue.md";

    public static readonly ErasureHolder[] Table =
    [
        new("ordering", "ordering-privacy"),
        new("payments", "payments-privacy"),
        new("shipping", "shipping-privacy"),
        new("notifications", "notifications-privacy"),
        new("bff", "bff-privacy"),
    ];
}
