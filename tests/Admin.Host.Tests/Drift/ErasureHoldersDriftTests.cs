using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Privacy;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="ErasureHolders.Table"/> against the holders Privacy asks (<c>Privacy__Responders__n</c> in the backend's
/// <c>deploy/compose/services/privacy.yml</c>, ADR-092): the same names in the same order, each with a queue the
/// console already lists, and the runbook an overdue request links to still there.
/// </summary>
public sealed partial class ErasureHoldersDriftTests
{
    [Fact]
    public void The_table_names_the_holders_privacy_asks_and_no_other()
    {
        string unit = Backend.Read("deploy", "compose", "services", "privacy.yml");
        string[] responders = [.. Responder().Matches(unit).Select(m => m.Groups[1].Value)];

        responders.ShouldNotBeEmpty("privacy.yml no longer names its responders as this test reads them");
        ErasureHolders.Table.Select(h => h.Name).ShouldBe(responders);
    }

    [Fact]
    public void Every_holder_queue_and_the_completions_queue_is_a_queue_the_broker_screen_lists()
    {
        HashSet<string> listed = [.. PlatformQueues.Table.Select(q => q.Name)];

        ErasureHolders.Table.Select(h => h.Queue).Where(q => !listed.Contains(q)).ShouldBeEmpty();
        listed.ShouldContain(ErasureHolders.CompletionsQueue);
    }

    [Fact]
    public void The_overdue_runbook_is_one_the_backend_still_has()
    {
        File.Exists(Path.Combine(Backend.Dir, "docs", "runbooks", ErasureHolders.OverdueRunbook)).ShouldBeTrue();
    }

    [GeneratedRegex(@"Privacy__Responders__\d+:\s*""([^""]+)""")]
    private static partial Regex Responder();
}
