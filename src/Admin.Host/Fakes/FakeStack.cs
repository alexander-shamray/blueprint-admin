namespace Admin.Host.Fakes;

/// <summary>
/// What a Scenario's lease has done to the fake stack (spec §5.3): whether Inventory is stopped and whether Ordering was
/// recreated with the short-timeouts override, and which alerts a game-day cause (spec §5.14) holds firing. The scripted <c>docker</c> commands set them (<see cref="FakePlatformScripts"/>)
/// and <see cref="FakeOrders"/> reads them when an order is placed, so a script's cause has its effect on a fake order as it has
/// on a stack. A load run's k6 block puts the golden-signal strip under load for <see cref="LoadWindow"/>, which
/// <see cref="FakeGrafana"/> reads; a stress run's up puts the stack on the stress override until an up without it or a down,
/// and its k6 block sets the users the strip scales with. Written by hand; a recording holds one instant and these have to change.
/// </summary>
public sealed class FakeStack(TimeProvider? time = null)
{
    /// <summary>
    /// How long the strip reads the recording made under load once a fake load run's k6 block has run: long enough for the Stack
    /// screen's 15-second poll to read it more than once, short enough that a later test on the same host reads the idle
    /// recording again. A <c>down</c> ends it sooner (<see cref="Unload"/>).
    /// </summary>
    public static readonly TimeSpan LoadWindow = TimeSpan.FromMinutes(1);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private volatile bool inventoryStopped;
    private volatile bool shortWaits;
    private volatile bool stressOverride;
    private volatile int stressUsers;
    private long loadedUntilTicks;
    private readonly Lock gate = new();
    private readonly HashSet<string> causedAlerts = new(StringComparer.Ordinal);

    /// <summary>Whether a load run's traffic is still inside the strip's reads (<see cref="LoadWindow"/>).</summary>
    public bool Loaded => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref loadedUntilTicks);

    /// <summary>A load run's k6 block ran: the strip reads the recording made under load until <see cref="LoadWindow"/> has passed.</summary>
    public void Load() => Interlocked.Exchange(ref loadedUntilTicks, (clock.GetUtcNow() + LoadWindow).UtcTicks);

    /// <summary>
    /// The stack came down: Compose removed the <c>grafana</c> container, whose Prometheus keeps its samples in the container
    /// and in no volume (backend <c>deploy/compose/infrastructure.yml</c>), so the strip reads no load after the next Up.
    /// </summary>
    public void Unload() => Interlocked.Exchange(ref loadedUntilTicks, 0);

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

    /// <summary>Whether the services the stress override changes were last recreated with it (<see cref="FakePlatformScripts"/>).</summary>
    public bool StressOverride
    {
        get => stressOverride;
        set => stressOverride = value;
    }

    /// <summary>The users a fake stress run's k6 is running, which the strip scales with; 0 when none is (<see cref="StressRunRecordings.Run"/>).</summary>
    public int StressUsers
    {
        get => stressUsers;
        set => stressUsers = value;
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
