using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>
/// What the scripted game-day tool prints, as <c>tools/game-day/game_day.py</c> words it (<c>GameDayDriftTests</c> holds those words
/// to the tool): the lines the Runbooks tile's countdown reads, with the windows the recorded listing gives.
/// </summary>
public sealed class RunbookRecordingsTests
{
    [Fact]
    public void A_cause_says_which_alert_it_causes_and_its_window_and_puts_it_in_the_state_it_fires_from()
    {
        FakeStack stack = new();

        string[] lines = [.. RunbookRecordings.Run(["outbox-growth", "--cause"], stack)];

        lines.ShouldBe(
        [
            "outbox-growth: causing OutboxGrowth; it fires within 780s once caused",
            "OutboxGrowth: the cause is running in the background (pid 4242); --restore ends it",
            "game day: cause OK in 0m05s",
        ]);
        stack.CausedAlerts.ShouldBe(["OutboxGrowth"]);
    }

    [Fact]
    public void A_cause_that_is_not_sustained_says_no_more_than_it_is_caused()
    {
        string[] lines = [.. RunbookRecordings.Run(["contact-refused", "--cause"], new FakeStack())];

        lines.ShouldBe(["contact-refused: causing ContactReadRefused; it fires within 300s once caused", "game day: cause OK in 0m05s"]);
    }

    [Fact]
    public void A_cause_of_the_second_phase_names_that_phase()
    {
        string[] lines = [.. RunbookRecordings.Run(["error-rate", "--cause", "--phase", "ErrorRateService"], new FakeStack())];

        lines[0].ShouldBe("error-rate: causing ErrorRateService; it fires within 480s once caused");
    }

    [Fact]
    public void A_restore_says_each_phases_state_and_names_a_window_only_for_a_runbook_that_settles_on_its_own()
    {
        FakeStack stack = new();
        stack.Cause(["ErrorRateGateway", "OutboxGrowth"]);

        string[] settles = [.. RunbookRecordings.Run(["error-rate", "--restore"], stack)];
        string[] plain = [.. RunbookRecordings.Run(["outbox-growth", "--restore"], new FakeStack())];

        settles.ShouldBe(
        [
            "ErrorRateGateway is firing after the restore; its window clears it within 480s",
            "ErrorRateService is inactive after the restore; its window clears it within 480s",
            "game day: restore OK in 0m04s",
        ]);
        plain.ShouldBe(["OutboxGrowth is inactive after the restore", "game day: restore OK in 0m04s"]);
        stack.CausedAlerts.ShouldBe(["OutboxGrowth"]);
    }
}
