using Admin.Host.Compose;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>One change a Scenario's lease may make: what it does, what undoes it, and how long it may be held.</summary>
/// <param name="Id">The name a request and the journal carry.</param>
/// <param name="Service">The one Compose service the change touches, which the restore is verified against.</param>
/// <param name="MaxHold">The longest a lease for this change may be asked to last; its deadline restores it by itself.</param>
/// <param name="Apply">Makes the change.</param>
/// <param name="Restore">Undoes it, and is safe to run when nothing was changed.</param>
public sealed record ScenarioChange(
    string Id,
    string Title,
    string Service,
    string Description,
    TimeSpan MaxHold,
    Func<ComposeService, Job> Apply,
    Func<ComposeService, Job> Restore);

/// <summary>
/// The only service changes the console makes beyond Up, Down and the BFF rebuild (spec §5.3, <em>Scenario service
/// control</em>): two, each a cause <c>run-locally.md</c> gives for a branch of the saga. A new one is a row here, a
/// paragraph of that document first, and a change to the spec's paragraph before either.
/// </summary>
public static class ScenarioControl
{
    public const string StopInventory = "stop-inventory";

    public const string ShortWaits = "short-waits";

    /// <summary>The longest any lease may last; <see cref="ScenarioChange.MaxHold"/> is at most this.</summary>
    public static readonly TimeSpan LongestHold = TimeSpan.FromMinutes(10);

    public static readonly IReadOnlyDictionary<string, ScenarioChange> Table = new Dictionary<string, ScenarioChange>(StringComparer.Ordinal)
    {
        // run-locally.md: "Stop Inventory, from blueprint-backend, and start it again when you are done", and "Bring Inventory back".
        [StopInventory] = new(
            StopInventory,
            "Inventory stopped",
            "inventory-api",
            "Stops inventory-api, so nothing answers a stock reservation or a release; the lease starts it again.",
            LongestHold,
            compose => compose.Stop("inventory-api"),
            compose => compose.Recreate("inventory-api", shortTimeouts: false)),

        // run-locally.md: "Shorten the saga's waits on Ordering alone" and "Restore the normal waits". The values are the
        // override file's (blueprint-backend ADR-128); none is set here, and only an order placed afterwards waits that long.
        [ShortWaits] = new(
            ShortWaits,
            "The saga's waits shortened",
            "ordering-api",
            "Recreates ordering-api with the backend's short-timeouts override, so the saga's waits run in seconds; the lease recreates it without.",
            LongestHold,
            compose => compose.Recreate("ordering-api", shortTimeouts: true),
            compose => compose.Recreate("ordering-api", shortTimeouts: false)),
    };
}
