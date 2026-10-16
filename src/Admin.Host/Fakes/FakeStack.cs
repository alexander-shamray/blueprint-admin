namespace Admin.Host.Fakes;

/// <summary>
/// What a Scenario's lease has done to the fake stack (spec §5.3): whether Inventory is stopped and whether Ordering was
/// recreated with the short-timeouts override. The scripted <c>docker</c> commands set them (<see cref="FakePlatformScripts"/>)
/// and <see cref="FakeOrders"/> reads them when an order is placed, so a script's cause has its effect on a fake order as it has
/// on a stack. Written by hand; a recording holds one instant and these have to change.
/// </summary>
public sealed class FakeStack
{
    private volatile bool inventoryStopped;
    private volatile bool shortWaits;

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
}
