using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// What <see cref="DeadLetterService"/>, its contracts and <see cref="DeadLetterRecordings"/> copy from blueprint-backend's
/// <c>tools/dead-letters</c>: where the tool is, the account and the variable it runs as, the prefix of its last-copy
/// line, and the JSON contract the console reads, held to the tool's README, its code and the broker's definitions.
/// </summary>
public sealed partial class DeadLetterDriftTests
{
    private static string Readme => Backend.Read("tools", "dead-letters", "README.md");

    [Fact]
    public void The_tool_is_where_the_console_runs_it()
    {
        File.Exists(Path.Combine(Backend.Dir, DeadLetterService.Tool)).ShouldBeTrue(DeadLetterService.Tool);
    }

    [Fact]
    public void The_operator_is_declared_on_the_broker_with_no_password_and_the_tool_runs_as_it()
    {
        using JsonDocument definitions = JsonDocument.Parse(Backend.Read("deploy", "compose", "rabbitmq", "definitions.json"));
        JsonElement user = definitions.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("name").GetString() == DeadLetterService.Operator);

        user.GetProperty("password_hash").GetString().ShouldBeEmpty("the operator ships with no password, which is what a per-run password relies on");
        Backend.Read(DeadLetterService.Tool).ShouldContain($"OPERATOR = \"{DeadLetterService.Operator}\"");
    }

    [Fact]
    public void The_password_is_read_from_the_variable_the_console_sets()
    {
        Backend.Read(DeadLetterService.Tool).ShouldContain($"environ.get(\"{DeadLetterService.PasswordVariable}\")");
        Readme.ShouldContain($"`{DeadLetterService.PasswordVariable}`");
    }

    [Fact]
    public void The_last_copy_line_carries_the_prefix_the_console_keeps()
    {
        Backend.Read(DeadLetterService.Tool).ShouldContain($"print(\"{DeadLetterService.UnrecordedPrefix}\" + json.dumps(");
    }

    [Fact]
    public void The_contract_is_schema_one_and_names_every_field_the_console_reads_and_the_fake_writes()
    {
        Readme.ShouldContain("`schema` is `1`");
        Match row = InspectRow().Match(Readme);
        row.Success.ShouldBeTrue("the README's `inspect` row of the JSON contract was not found");
        HashSet<string> fields = [.. Field().Matches(row.Value).Select(m => m.Groups[1].Value)];

        string[] read = ["message_id", "correlation_id", "message_type", "fault_message", "fault_exception_type", "input_address", "reason", "redelivered", "body"];
        read.Where(f => !fields.Contains(f)).ShouldBeEmpty();

        using JsonDocument fake = JsonDocument.Parse(string.Join('\n', DeadLetterRecordings.Inspect(["ordering-catalog-events_error"])));
        fake.RootElement.EnumerateObject().Select(p => p.Name).Where(f => !fields.Contains(f)).ShouldBeEmpty();
        fake.RootElement.GetProperty("messages")[0].EnumerateObject().Select(p => p.Name).Where(f => !fields.Contains(f)).ShouldBeEmpty();
    }

    [Fact]
    public void The_fakes_dry_run_line_is_the_one_the_tool_prints()
    {
        Backend.Read(DeadLetterService.Tool).ShouldContain("dry run: nothing left {queue}. Pass --execute --record FILE to {verb}; the read ");
    }

    [GeneratedRegex(@"^\| `inspect` \|.*$", RegexOptions.Multiline)]
    private static partial Regex InspectRow();

    /// <summary>A field as the contract table spells it; an array is written <c>`messages[]`</c>.</summary>
    [GeneratedRegex(@"`([a-z0-9_]+)(?:\[\])?`")]
    private static partial Regex Field();
}
