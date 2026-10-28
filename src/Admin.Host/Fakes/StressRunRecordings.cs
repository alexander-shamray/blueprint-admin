using System.Globalization;
using System.Text.RegularExpressions;

namespace Admin.Host.Fakes;

/// <summary>
/// What run-locally.md's "Stress run (k6)" blocks print in FakePlatform mode. <b>Written by hand, not recorded</b>: a live run
/// takes a rebuild and seventeen minutes, and none has been recorded here yet. The shapes are the backend's: k6's banner and its
/// once-a-second <c>running (…), NNN/MMM VUs, …</c> and <c>journey [ … ]</c> lines as k6 v2.2.0 prints them when its output is
/// not a terminal (probed on 2026-10-27 with a ramping-vus script of the same shape), <c>handleSummary()</c>'s line from
/// <c>stress.js</c>, the sampler's last line from <c>sample.mjs</c>, and the <c>Knee:</c> and <c>Summary:</c> lines of
/// <c>summarise.mjs</c>. The staircase is the one the run's <c>STRESS_PROFILE</c> names, each step cut to <see cref="RampTicks"/> and
/// <see cref="HoldTicks"/> lines. Each level has its break (<see cref="Plan"/>), and the run ends as the stop rule says: at the
/// break under <c>knee</c> with 108, past the last step under <c>errors</c> with 99, and <c>xhigh</c> on the generator, which
/// cannot hold its users. Exact text is not a contract.
/// </summary>
internal static partial class StressRunRecordings
{
    /// <summary>
    /// Owner: blueprint-backend <c>deploy/observability/README.md</c>, "The stress run": each level's steps, and where the fake
    /// breaks on it as a 1-based step. A level the owner's table gives no break is one the fake invents.
    /// </summary>
    internal static readonly Dictionary<string, (int[] Steps, int Break)> Levels = new(StringComparer.Ordinal)
    {
        ["low"] = ([100, 200, 400, 800], 2),
        ["medium"] = ([800, 1600, 3200], 1),
        ["high"] = ([3200, 6400, 12800], 2),
        ["xhigh"] = ([12800, 25600], 1),
    };

    /// <summary>What k6 prints as the most VUs when <c>STRESS_MAX_USERS</c> is not set: the owner's default cap.</summary>
    private const int DefaultCap = 12800;

    /// <summary>Progress ticks a ramp takes, cut from the default's thirty.</summary>
    public const int RampTicks = 3;

    /// <summary>Progress ticks a hold takes, cut from the default's 180, and enough for the console to read a hold.</summary>
    public const int HoldTicks = 30;

    /// <summary>How far apart the fake k6 prints its lines: the run lasts long enough for the Stack screen's 3-second poll to read each hold.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(100);

    /// <summary>The up with the stress override, recreating the three services it changes.</summary>
    public static readonly string Up =
        """
         Container commerce-sql-1  Healthy
         Container commerce-rabbitmq-1  Healthy
         Container commerce-keycloak-1  Healthy
         Container commerce-gateway-1  Recreated
         Container commerce-shipping-worker-1  Recreated
         Container commerce-notifications-worker-1  Recreated
         Container commerce-gateway-1  Healthy
         Container commerce-shipping-worker-1  Healthy
         Container commerce-notifications-worker-1  Healthy
        """;

    /// <summary>
    /// What a fake run does on a level under a stop rule, read from the run block's variables as k6 would: the steps it climbs, the
    /// step it ends at and why, the exit code, and the lines summarise.mjs prints about it.
    /// </summary>
    public sealed record Plan(string Level, string Stop, int[] Steps, int Cap, int EndStep, string Reason, int ExitCode)
    {
        /// <summary>The step the level breaks at, 1-based; the generator's cannot hold its users, so its step is not measured.</summary>
        private int Break => Levels[Level].Break;

        public int Users => Steps[EndStep - 1];

        public string KneeLine => Reason == "generator"
            ? "Knee: none; every measured step held."
            : $"Knee: step {Break} ({Steps[Break - 1]} VUs): failures, 23.4% of requests failed (http 5xx 1912, timeout 388)";

        public string FirstToGiveUpLine => Reason == "generator" ? "First to give up: none named" : "First to give up: Ordering.Api: 5xx 3.2% | sql: 4 deadlocks";

        public string EndedLine => Reason == "generator"
            ? $"Ended: generator at step {EndStep} ({Users} VUs): k6 ran {Users * 4 / 5} of {Users} VUs; level {Level}, stop rule {Stop}"
            : $"Ended: {Reason} at step {EndStep} ({Users} VUs); level {Level}, stop rule {Stop}";
    }

    /// <summary>The plan a run block's environment asks for: <c>STRESS_PROFILE</c>, <c>STRESS_STOP</c> and <c>STRESS_MAX_USERS</c>, each as its default when unset.</summary>
    public static Plan PlanFor(IReadOnlyDictionary<string, string>? environment)
    {
        string level = environment is not null && environment.TryGetValue("STRESS_PROFILE", out string? asked) && Levels.ContainsKey(asked) ? asked : "low";
        string stop = environment is not null && environment.TryGetValue("STRESS_STOP", out string? rule) && rule == "errors" ? "errors" : "knee";
        int cap = environment is not null && environment.TryGetValue("STRESS_MAX_USERS", out string? max) && int.TryParse(max, CultureInfo.InvariantCulture, out int parsed) ? parsed : DefaultCap;
        (int[] steps, int broke) = Levels[level];

        return level == "xhigh"
            ? new Plan(level, stop, steps, cap, 1, "generator", 0)
            : stop == "knee"
                ? new Plan(level, stop, steps, cap, broke, "knee", 108)
                : new Plan(level, stop, steps, cap, steps.Length, "errors", 99);
    }

    private const string Out = "artifacts/stress/20261027-150000";

    /// <summary>The summary folder a plan's run writes into, named by what was asked so the fake summary can say the same.</summary>
    private static string OutFor(Plan plan) => $"{Out}-{plan.Level}-{plan.Stop}";

    /// <summary>
    /// The run block's lines for <paramref name="environment"/>'s plan, k6's progress among them, setting
    /// <see cref="FakeStack.StressUsers"/> as each progress line is printed so the fake strip rises with the steps; the count goes
    /// back to none when the run ends or is stopped. The exit code is the plan's.
    /// </summary>
    public static (IEnumerable<string> Lines, int ExitCode) Run(FakeStack stack, IReadOnlyDictionary<string, string>? environment)
    {
        Plan plan = PlanFor(environment);

        return (Lines(stack, plan), plan.ExitCode);
    }

    private static IEnumerable<string> Lines(FakeStack stack, Plan plan)
    {
        try
        {
            string folder = OutFor(plan);
            int planned = plan.Steps.Length * (RampTicks + HoldTicks);

            yield return "currency: EUR";
            yield return "products: 5eed0000-0000-0000-0000-000000000001,5eed0000-0000-0000-0000-000000000002,5eed0000-0000-0000-0000-000000000004";
            yield return "";
            yield return "         /\\      Grafana   /‾‾/  ";
            yield return "";
            yield return "     execution: local";
            yield return "        script: deploy/observability/stress/stress.js";
            yield return "        output: -";
            yield return "";
            yield return $"     scenarios: (100.00%) 1 scenario, {plan.Cap} max VUs, {Elapsed(planned + 20)} max duration (incl. graceful stop):";
            yield return $"              * journey: Up to {plan.Cap} looping VUs for {Elapsed(planned)} over {2 * plan.Steps.Length} stages (gracefulRampDown: 10s, exec: session, gracefulStop: 10s)";
            yield return "";

            int tick = 0;
            int from = 0;
            int complete = 0;

            foreach (int target in plan.Steps.Take(plan.EndStep))
            {
                for (int i = 1; i <= RampTicks + HoldTicks; i++)
                {
                    int users = i <= RampTicks ? from + ((target - from) * i / RampTicks) : target;
                    tick++;
                    complete += users / 2;
                    stack.StressUsers = Math.Min(users, 400);

                    yield return $"running ({Elapsed(tick)}), {users:000}/{plan.Cap} VUs, {complete} complete and 0 interrupted iterations";
                    yield return $"journey   [ {Percent(tick, planned),3}% ] {users:000}/{plan.Cap} VUs  {Elapsed(tick)}/{Elapsed(planned)}";
                }

                from = target;
            }

            stack.StressUsers = 0;
            yield return $"running ({Elapsed(tick + 1)}), 000/{plan.Cap} VUs, {complete} complete and 194 interrupted iterations";
            yield return $"journey ✗ [ {Percent(tick, planned),3}% ] 000/{plan.Cap} VUs  {Elapsed(tick + 1)}/{Elapsed(planned)}";
            yield return "";
            yield return $"Stress run: {plan.EndStep} of {plan.Steps.Length} steps measured; raw results in {folder}/raw.json. Next: node deploy/observability/stress/summarise.mjs {folder}";
            yield return $"k6 exit: {plan.ExitCode}";
            yield return "sampler: stopped, k6 wrote its results";
            yield return plan.KneeLine;
            yield return plan.FirstToGiveUpLine;
            yield return plan.EndedLine;
            yield return $"Summary: {folder.Replace('/', '\\')}\\summary.md";
        }
        finally
        {
            stack.StressUsers = 0;
        }
    }

    /// <summary>The summary summarise.mjs writes, as the fake's for a path: one row a step of the plan its folder names, the break's marked.</summary>
    public static IReadOnlyList<string>? ReadSummary(string path)
    {
        Match m = Folder().Match(path);
        Plan plan = PlanFor(m.Success
            ? new Dictionary<string, string> { ["STRESS_PROFILE"] = m.Groups["level"].Value, ["STRESS_STOP"] = m.Groups["stop"].Value }
            : null);

        return
        [
            "# Stress run, 2026-10-27",
            "",
            plan.KneeLine,
            plan.FirstToGiveUpLine,
            plan.EndedLine,
            "",
            "| Step | VUs | Requests/s | p95 ms | p99 ms | Failed |",
            "|---|---|---|---|---|---|",
            .. plan.Steps.Take(plan.EndStep).Select((users, i) =>
                $"| {i + 1} | {users} | {users / 4.0:0.0} | {48 * (i + 1) * (i + 1)} | {91 * (i + 1) * (i + 1)} | {(i + 1 == plan.EndStep && plan.Reason != "generator" ? "23.4% (past the abort rate)" : "0.0%")} |"),
        ];
    }

    [GeneratedRegex(@"20261027-150000-(?<level>[a-z]+)-(?<stop>[a-z]+)")]
    private static partial Regex Folder();

    /// <summary>How far through the planned ticks a tick is, as k6's bar prints it.</summary>
    private static int Percent(int tick, int planned) => 100 * tick / planned;

    /// <summary>
    /// k6's elapsed time as it prints it, at the default's pace of one tick a second. A run whose most duration passes a minute
    /// prints minutes from the first tick, as the load run's recorded <c>0m01.0s</c> does, padded here to the two digits a
    /// 17m40s run's minutes take.
    /// </summary>
    private static string Elapsed(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}m{seconds % 60:00}.0s");
}
