using System.Globalization;

namespace Admin.Host.Fakes;

/// <summary>
/// What run-locally.md's "Stress run (k6)" blocks print in FakePlatform mode. <b>Written by hand, not recorded</b>: a live run
/// takes a rebuild and seventeen minutes, and none has been recorded here yet. The shapes are the backend's: k6's banner and its
/// once-a-second <c>running (…), NNN/MMM VUs, …</c> and <c>journey [ … ]</c> lines as k6 v2.2.0 prints them when its output is
/// not a terminal (probed on 2026-10-27 with a ramping-vus script of the same shape), <c>handleSummary()</c>'s line from
/// <c>stress.js</c>, the sampler's last line from <c>sample.mjs</c>, and the <c>Knee:</c> and <c>Summary:</c> lines of
/// <c>summarise.mjs</c>. The staircase is the default one (10, 50, 100, 200, 400) cut to <see cref="RampTicks"/> and
/// <see cref="HoldTicks"/> lines a step, and it breaks at step 4, so k6 stops there with 99. Exact text is not a contract.
/// </summary>
internal static class StressRunRecordings
{
    /// <summary>The steps the fake staircase climbs before the break, the default's first four.</summary>
    public static readonly int[] Steps = [10, 50, 100, 200];

    /// <summary>The default staircase's top, which k6 prints as the most VUs.</summary>
    public const int MaxUsers = 400;

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

    /// <summary>The knee summarise.mjs names: the first step a signal crossed at, and the signal.</summary>
    public const string Knee = "Knee: step 4 (200 VUs): failures, 23.4% of requests failed (http 5xx 1912, timeout 388)";

    private const string Out = "artifacts/stress/20261027-150000";

    /// <summary>
    /// The run block's lines, k6's progress among them, setting <see cref="FakeStack.StressUsers"/> as each progress line is
    /// printed so the fake strip rises with the steps; the count goes back to none when the run ends or is stopped.
    /// </summary>
    public static IEnumerable<string> Run(FakeStack stack)
    {
        try
        {
            yield return "currency: EUR";
            yield return "products: 5eed0000-0000-0000-0000-000000000001,5eed0000-0000-0000-0000-000000000002,5eed0000-0000-0000-0000-000000000004";
            yield return "";
            yield return "         /\\      Grafana   /‾‾/  ";
            yield return "";
            yield return "     execution: local";
            yield return "        script: deploy/observability/stress/stress.js";
            yield return "        output: -";
            yield return "";
            yield return $"     scenarios: (100.00%) 1 scenario, {MaxUsers} max VUs, 17m40s max duration (incl. graceful stop):";
            yield return $"              * journey: Up to {MaxUsers} looping VUs for 17m30s over 10 stages (gracefulRampDown: 10s, exec: session, gracefulStop: 10s)";
            yield return "";

            int tick = 0;
            int from = 0;
            int complete = 0;

            foreach (int target in Steps)
            {
                for (int i = 1; i <= RampTicks + HoldTicks; i++)
                {
                    int users = i <= RampTicks ? from + ((target - from) * i / RampTicks) : target;
                    tick++;
                    complete += users / 2;
                    stack.StressUsers = users;

                    yield return $"running ({Elapsed(tick)}), {users:000}/{MaxUsers} VUs, {complete} complete and 0 interrupted iterations";
                    yield return $"journey   [ {Percent(tick),3}% ] {users:000}/{MaxUsers} VUs  {Elapsed(tick)}/17m30s";
                }

                from = target;
            }

            stack.StressUsers = 0;
            yield return $"running ({Elapsed(tick + 1)}), 000/{MaxUsers} VUs, {complete} complete and 194 interrupted iterations";
            yield return $"journey ✗ [ {Percent(tick),3}% ] 000/{MaxUsers} VUs  {Elapsed(tick + 1)}/17m30s";
            yield return "";
            yield return $"Stress run: 4 of 5 steps measured; raw results in {Out}/raw.json. Next: node deploy/observability/stress/summarise.mjs {Out}";
            yield return "k6 exit: 99";
            yield return "sampler: stopped, k6 wrote its results";
            yield return Knee;
            yield return $"Summary: {Out.Replace('/', '\\')}\\summary.md";
        }
        finally
        {
            stack.StressUsers = 0;
        }
    }

    /// <summary>The summary summarise.mjs writes, as the fake's for any path: one row a step, the knee's marked.</summary>
    public static IReadOnlyList<string>? ReadSummary(string path) =>
    [
        "# Stress run, 2026-10-27",
        "",
        Knee,
        "",
        "| Step | VUs | Requests/s | p95 ms | p99 ms | Failed |",
        "|---|---|---|---|---|---|",
        "| 1 | 10 | 4.1 | 48 | 91 | 0.0% |",
        "| 2 | 50 | 20.3 | 61 | 140 | 0.0% |",
        "| 3 | 100 | 39.8 | 212 | 655 | 0.4% |",
        "| 4 | 200 | 52.6 | 4210 | 9870 | 23.4% (past the abort rate) |",
    ];

    /// <summary>How far through the default's five steps a tick is, as k6's bar prints it.</summary>
    private static int Percent(int tick) => 100 * tick / (5 * (RampTicks + HoldTicks));

    /// <summary>
    /// k6's elapsed time as it prints it, at the default's pace of one tick a second. A run whose most duration passes a minute
    /// prints minutes from the first tick, as the load run's recorded <c>0m01.0s</c> does, padded here to the two digits a
    /// 17m40s run's minutes take.
    /// </summary>
    private static string Elapsed(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}m{seconds % 60:00}.0s");
}
