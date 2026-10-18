namespace Admin.Host.Fakes;

/// <summary>
/// What a Scenario's lease has done to the fake stack (spec §5.3): whether Inventory is stopped and whether Ordering was
/// recreated with the short-timeouts override, and which alerts a game-day cause (spec §5.14) holds firing. The scripted <c>docker</c> commands set them (<see cref="FakePlatformScripts"/>)
/// and <see cref="FakeOrders"/> reads them when an order is placed, so a script's cause has its effect on a fake order as it has
/// on a stack. A load run's k6 block puts the golden-signal strip under load for <see cref="LoadWindow"/>, which
/// <see cref="FakeGrafana"/> reads. Written by hand; a recording holds one instant and these have to change.
/// </summary>
public sealed class FakeStack(TimeProvider? time = null)
{
    /// <summary>
    /// How long the strip reads the recording made under load once a fake load run's k6 block has run: long enough for the Stack
    /// screen's 15-second poll to read it more than once, short enough that a later test on the same host reads the idle
    /// recording again. A host reused across Playwright runs (<c>reuseExistingServer</c>) inside the minute still reads the load.
    /// </summary>
    public static readonly TimeSpan LoadWindow = TimeSpan.FromMinutes(1);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private volatile bool inventoryStopped;
    private volatile bool shortWaits;
    private long loadedUntilTicks;
    private readonly Lock gate = new();
    private readonly HashSet<string> causedAlerts = new(StringComparer.Ordinal);

    /// <summary>Whether a load run's traffic is still inside the strip's reads (<see cref="LoadWindow"/>).</summary>
    public bool Loaded => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref loadedUntilTicks);

    /// <summary>A load run's k6 block ran: the strip reads the recording made under load until <see cref="LoadWindow"/> has passed.</summary>
    public void Load() => Interlocked.Exchange(ref loadedUntilTicks, (clock.GetUtcNow() + LoadWindow).UtcTicks);

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
