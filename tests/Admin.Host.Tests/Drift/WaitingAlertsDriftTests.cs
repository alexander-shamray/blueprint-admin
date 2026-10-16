using System.Text.RegularExpressions;
using Admin.Host.Telemetry;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="WaitingAlerts"/> against the backend's <c>awaiting-signal.yaml</c>, both ways, and against
/// <c>platform-alerts.yaml</c>, which must not hold a rule the strip calls waiting.
/// </summary>
public sealed partial class WaitingAlertsDriftTests
{
    private const string Awaiting = "awaiting-signal.yaml";
    private const string Loaded = "platform-alerts.yaml";

    [Fact]
    public void The_waiting_table_names_exactly_the_rules_the_backend_has_not_loaded()
    {
        Rules(Awaiting).Keys.Order(StringComparer.Ordinal)
            .ShouldBe(WaitingAlerts.Table.Select(w => w.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_instrument_is_read_by_the_expression_of_the_rule_that_names_it()
    {
        Dictionary<string, string> rules = Rules(Awaiting);

        foreach (WaitingAlert waiting in WaitingAlerts.Table)
        {
            foreach (string instrument in waiting.Instruments)
            {
                rules[waiting.Name].ShouldContain(instrument, Case.Sensitive, waiting.Name);
            }
        }
    }

    [Fact]
    public void No_waiting_rule_is_also_loaded()
    {
        Rules(Loaded).Keys.Intersect(WaitingAlerts.Table.Select(w => w.Name)).ShouldBeEmpty();
    }

    /// <summary>Each rule's name to its text up to the next rule: the expression and everything around it.</summary>
    private static Dictionary<string, string> Rules(string file)
    {
        string text = Backend.Read("deploy", "observability", "alerts", file);
        MatchCollection names = RuleName().Matches(text);
        Dictionary<string, string> rules = new(StringComparer.Ordinal);

        for (int i = 0; i < names.Count; i++)
        {
            int end = i + 1 < names.Count ? names[i + 1].Index : text.Length;
            rules[names[i].Groups["name"].Value] = text[names[i].Index..end];
        }

        return rules;
    }

    [GeneratedRegex(@"^\s*- alert:\s*(?<name>\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RuleName();
}
