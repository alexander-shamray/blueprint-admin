using Admin.Host.Fakes;
using Admin.Host.Runbooks;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The lines of blueprint-backend's <c>tools/game-day/game_day.py</c> that the Runbooks tile's countdown reads
/// (<c>game-day-lines.ts</c>, <c>GameDayLineReader</c>) and <see cref="RunbookRecordings"/> prints: a cause's <c>causing</c> line and a
/// restore's <c>after the restore</c> line. A line the tool words differently would leave the countdown with nothing to read,
/// so each is held to the tool's source here, as the fake's output is held to the same words in <c>RunbookRecordingsTests</c>.
/// </summary>
public sealed class GameDayDriftTests
{
    private static string Tool => Backend.Read(GameDayService.Tool);

    [Fact]
    public void The_tool_is_where_the_console_runs_it()
    {
        File.Exists(Path.Combine(Backend.Dir, GameDayService.Tool)).ShouldBeTrue(GameDayService.Tool);
    }

    /// <summary>The line a cause prints once the quiet start has passed, which opens the fire countdown.</summary>
    [Fact]
    public void A_cause_says_which_alert_it_causes_and_that_it_fires_within_a_window()
    {
        Tool.ShouldContain("f\"{scenario.RUNBOOK}: causing {alert}; it fires within {scenario.DEADLINE.seconds}s once caused\"");
    }

    /// <summary>The line a sustained cause adds, which the countdown leaves in the log.</summary>
    [Fact]
    public void A_sustained_cause_says_it_runs_in_the_background()
    {
        Tool.ShouldContain("f\"{alert}: the cause is running in the background (pid {pid}); --restore ends it\"");
    }

    /// <summary>The line a restore prints for each phase, which opens the clear countdown or ends it at once.</summary>
    [Fact]
    public void A_restore_says_what_state_each_alert_is_in_and_the_window_that_clears_it()
    {
        string tool = Tool;

        tool.ShouldContain("f\"; its window clears it within {window.seconds}s\"");
        tool.ShouldContain("f\"{phase.ALERT} is {world.alerts.state(phase.ALERT)} after the restore{clears}\"");
    }
}
