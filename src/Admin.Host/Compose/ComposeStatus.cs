namespace Admin.Host.Compose;

/// <summary>The stack as a whole. A Docker that does not answer is a state, not an exception (spec §9).</summary>
public sealed record ComposeStatus(bool Reachable, string? Error, IReadOnlyList<ServiceStatus> Services)
{
    public static ComposeStatus Up(IReadOnlyList<ServiceStatus> services) => new(true, null, services);

    public static ComposeStatus Unreachable(string error) => new(false, error, []);
}
