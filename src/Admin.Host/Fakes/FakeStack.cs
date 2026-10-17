namespace Admin.Host.Fakes;

/// <summary>
/// What a Scenario's lease has done to the fake stack (spec §5.3): whether Inventory is stopped and whether Ordering was
/// recreated with the short-timeouts override, and which alerts a game-day cause (spec §5.14) holds firing. The scripted <c>docker</c> commands set them (<see cref="FakePlatformScripts"/>)
/// and <see cref="FakeOrders"/> reads them when an order is placed, so a script's cause has its effect on a fake order as it has
/// on a stack. Written by hand; a recording holds one instant and these have to change.
/// </summary>
public sealed class FakeStack
{
    private volatile bool inventoryStopped;
    private volatile bool shortWaits;
    private readonly Lock gate = new();
    private readonly HashSet<string> causedAlerts = new(StringComparer.Ordinal);

    public bool InventoryStopped
    {
        get => inventoryStopped;
        set => inventoryStopped = value;
    }

    public bool ShortWaits
    {
        get => shortWaits;
        set => shortWaits = value;
    }

    /// <summary>The alerts a game-day cause has put in the state it fires from, until its restore (<see cref="RunbookRecordings"/>).</summary>
    public IReadOnlySet<string> CausedAlerts
    {
        get
        {
            lock (gate)
            {
                return new HashSet<string>(causedAlerts, StringComparer.Ordinal);
            }
        }
    }

    public void Cause(IEnumerable<string> alerts)
    {
        lock (gate)
        {
            causedAlerts.UnionWith(alerts);
        }
    }

    public void Restore(IEnumerable<string> alerts)
    {
        lock (gate)
        {
            causedAlerts.ExceptWith(alerts);
        }
    }
}
