using System.Text.Json.Serialization;

namespace Admin.Host.Stack;

/// <summary>
/// How hard a stress run pushes and when it stops: the two variables run-locally.md's "Stress run (k6)" sets from the environment
/// before the run block, <c>STRESS_PROFILE</c> and <c>STRESS_STOP</c>. The run block stays the document's; the choice reaches it
/// through <see cref="Environment"/>. The levels' steps are the backend's (<c>deploy/observability/stress</c>) and are not copied.
/// </summary>
public sealed record StressChoice(string Level, string Stop)
{
    /// <summary>Owner: run-locally.md, "Stress run (k6)", the values <c>STRESS_PROFILE</c> takes.</summary>
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh"];

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the values <c>STRESS_STOP</c> takes; the first is the default rule.</summary>
    public static readonly IReadOnlyList<string> Stops = ["knee", "errors"];

    /// <summary>What a run is asked for when the request names nothing: the console's lowest level, stopping at the first knee.</summary>
    public static readonly StressChoice Default = new(Levels[0], Stops[0]);

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the level a cap of the default leaves one step of, which needs a larger cap.</summary>
    internal const string OpenLevel = "xhigh";

    /// <summary>Owner: run-locally.md, "Stress run (k6)": the least <c>STRESS_MAX_USERS</c> that leaves <see cref="OpenLevel"/> more than one step.</summary>
    internal const string OpenLevelCap = "25600";

    /// <summary>The variables run-locally.md's run block reads, set on its process on top of the host's own.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Environment
    {
        get
        {
            Dictionary<string, string> variables = new(StringComparer.Ordinal)
            {
                ["STRESS_PROFILE"] = Level,
                ["STRESS_STOP"] = Stop,
            };

            if (Level == OpenLevel)
            {
                variables["STRESS_MAX_USERS"] = OpenLevelCap;
            }

            return variables;
        }
    }

    /// <summary>
    /// The choice a request names, with <see cref="Default"/> for what it leaves out, or why it is refused: a value the backend
    /// does not name is never passed on, since k6 would refuse it only after the up and a minute idle.
    /// </summary>
    public static StressChoice? Parse(string? level, string? stop, out string? refusal)
    {
        refusal = null;

        if (level is not null && !Levels.Contains(level, StringComparer.Ordinal))
        {
            refusal = $"The level \"{level}\" is not one the backend names: {string.Join(", ", Levels)}.";

            return null;
        }

        if (stop is not null && !Stops.Contains(stop, StringComparer.Ordinal))
        {
            refusal = $"The stop rule \"{stop}\" is not one the backend names: {string.Join(", ", Stops)}.";

            return null;
        }

        return new StressChoice(level ?? Default.Level, stop ?? Default.Stop);
    }
}
