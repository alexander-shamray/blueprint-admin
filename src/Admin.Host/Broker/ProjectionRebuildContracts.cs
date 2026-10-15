namespace Admin.Host.Broker;

public enum RebuildMode
{
    /// <summary>The tool with no argument: sends what the BFF's inbox has not recorded and deletes nothing.</summary>
    Repair,

    /// <summary>The tool with <c>--reset</c>: deletes the projection, then replays the window. The BFF is stopped around it.</summary>
    Rebuild,
}

/// <summary>One thing the tool cannot do, shown before it runs, with the words of its README that the drift gate holds it to.</summary>
public sealed record ReplayLimit(string Text, string Anchor);

/// <summary>What the Broker screen says before either button is pressed (<c>GET /api/broker/projection</c>).</summary>
public sealed record ProjectionPlan(string Queue, string RebuildConfirm, IReadOnlyList<string> Limits);

public sealed record RebuildRequest(string? Confirm);

/// <summary>A run <see cref="ProjectionRebuildService.Start"/> began or found still going, with the kind it is.</summary>
public sealed record Started(Jobs.Job Job, RebuildMode Mode);
