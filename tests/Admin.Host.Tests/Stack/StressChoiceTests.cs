using Admin.Host.Fakes;
using Admin.Host.Stack;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class StressChoiceTests
{
    [Fact]
    public void Nothing_asked_is_the_default_and_a_half_asked_takes_the_default_for_the_rest()
    {
        StressChoice.Parse(null, null, out string? refusal).ShouldBe(new StressChoice("low", "knee"));
        refusal.ShouldBeNull();
        StressChoice.Parse("high", null, out _).ShouldBe(new StressChoice("high", "knee"));
        StressChoice.Parse(null, "errors", out _).ShouldBe(new StressChoice("low", "errors"));
    }

    [Theory]
    [InlineData("extreme", null, "level")]
    [InlineData("LOW", null, "level")]
    [InlineData("low", "never", "stop rule")]
    public void A_value_the_backend_does_not_name_is_refused_and_named(string? level, string? stop, string what)
    {
        StressChoice.Parse(level, stop, out string? refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain(what);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void A_level_the_default_cap_leaves_steps_of_sets_the_level_and_the_rule_and_no_cap(string level)
    {
        new StressChoice(level, "errors").Environment.ShouldBe(new Dictionary<string, string> { ["STRESS_PROFILE"] = level, ["STRESS_STOP"] = "errors" });
    }

    [Fact]
    public void The_open_level_also_raises_the_cap_run_locally_md_says_it_needs()
    {
        new StressChoice("xhigh", "knee").Environment["STRESS_MAX_USERS"].ShouldBe("25600");
    }

    [Theory]
    [InlineData("low", "knee", 108, "knee", 2)]
    [InlineData("low", "errors", 99, "errors", 4)]
    [InlineData("medium", "knee", 108, "knee", 1)]
    [InlineData("medium", "errors", 99, "errors", 3)]
    [InlineData("high", "knee", 108, "knee", 2)]
    [InlineData("xhigh", "knee", 0, "generator", 1)]
    [InlineData("xhigh", "errors", 0, "generator", 1)]
    public void The_fake_replays_each_level_and_both_stop_rules_with_the_ending_it_prints(string level, string stop, int exitCode, string reason, int endStep)
    {
        StressRunRecordings.Plan plan = StressRunRecordings.PlanFor(new StressChoice(level, stop).Environment);

        plan.ExitCode.ShouldBe(exitCode);
        plan.Reason.ShouldBe(reason);
        plan.EndStep.ShouldBe(endStep);
        plan.EndedLine.ShouldStartWith($"Ended: {reason} at step {endStep} (");
        plan.EndedLine.ShouldContain($"level {level}, stop rule {stop}");
    }

    [Fact]
    public void A_knee_stop_ends_with_the_service_that_gave_up_and_a_generator_stop_names_none()
    {
        StressRunRecordings.PlanFor(new StressChoice("low", "knee").Environment).FirstToGiveUpLine.ShouldStartWith("First to give up: Ordering.Api");
        StressRunRecordings.PlanFor(new StressChoice("xhigh", "knee").Environment).FirstToGiveUpLine.ShouldBe("First to give up: none named");
    }

    [Fact]
    public void The_fake_summary_is_the_plan_its_folder_names()
    {
        IReadOnlyList<string> summary = StressRunRecordings.ReadSummary(@"artifacts\stress\20261027-150000-medium-errors\summary.md")!;

        summary.ShouldContain("| 3 | 3200 | 800.0 | 432 | 819 | 23.4% (past the abort rate) |");
        summary.Count(l => l.StartsWith("| ", StringComparison.Ordinal) && char.IsDigit(l[2])).ShouldBe(3);
    }
}
