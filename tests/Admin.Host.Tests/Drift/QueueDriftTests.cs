using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="PlatformQueues.Table"/> against the queue constants the backend declares: every row names a
/// constant that still holds its queue, and every constant is a row or a named exception.
/// </summary>
public sealed partial class QueueDriftTests
{
    /// <summary>Queues the console does not show yet, each with the issue that adds it.</summary>
    private static readonly Dictionary<string, string> QueuesNotYetShown = new(StringComparer.Ordinal);

    [Fact]
    public void Every_row_names_a_backend_constant_that_still_holds_its_queue()
    {
        Dictionary<string, string> declared = Declared();

        foreach (PlatformQueue queue in PlatformQueues.Table)
        {
            declared.ShouldContainKey(queue.BackendSymbol);
            declared[queue.BackendSymbol].ShouldBe(queue.Name, queue.BackendSymbol);
        }
    }

    [Fact]
    public void Every_queue_the_backend_declares_is_a_row_or_a_named_exception()
    {
        HashSet<string> shown = [.. PlatformQueues.Table.Select(q => q.Name)];

        Dictionary<string, string> declared = Declared();

        declared.Values.Where(q => !shown.Contains(q) && !QueuesNotYetShown.ContainsKey(q)).ShouldBeEmpty();
        QueuesNotYetShown.Keys.Where(shown.Contains).ShouldBeEmpty("an exception for a queue that now has a row");
        QueuesNotYetShown.Keys.Where(q => !declared.ContainsValue(q)).ShouldBeEmpty("an exception for a queue the backend no longer declares");
    }

    [Fact]
    public void The_projection_is_a_row_of_the_table()
    {
        PlatformQueues.Table.Select(q => q.Name).ShouldContain(PlatformQueues.Projection);
    }

    [Fact]
    public void Every_parked_kind_is_a_suffix_the_dead_letter_tool_reads_and_nothing_it_reads_is_missing()
    {
        string tool = Backend.Read("tools", "dead-letters", "dead_letters.py");
        Match suffixes = ToolSuffixes().Match(tool);

        suffixes.Success.ShouldBeTrue("SUFFIXES was not found in dead_letters.py; the tool's shape has moved");

        Dictionary<string, string> read = ToolSuffix().Matches(suffixes.Groups[1].Value)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        ParkedQueues.All.ToDictionary(k => k.Suffix, k => k.Name, StringComparer.Ordinal).ShouldBe(read, ignoreOrder: true);
    }

    [Fact]
    public void Every_parked_kind_names_a_runbook_the_backend_still_has()
    {
        foreach (ParkedKind kind in ParkedQueues.All)
        {
            File.Exists(Path.Combine(Backend.Dir, "docs", "runbooks", kind.Runbook)).ShouldBeTrue(kind.Runbook);
        }
    }

    [GeneratedRegex(@"SUFFIXES\s*=\s*\{([^}]*)\}")]
    private static partial Regex ToolSuffixes();

    [GeneratedRegex(@"""(_\w+)""\s*:\s*""(\w+)""")]
    private static partial Regex ToolSuffix();

    /// <summary>
    /// Every <c>public const string …Queue = "…"</c> under the backend's src, keyed by the symbol the table cites:
    /// the file's namespace, its first top-level type and the field.
    /// </summary>
    private static Dictionary<string, string> Declared()
    {
        Dictionary<string, string> declared = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(Backend.Dir, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || file.Contains("Tests", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            MatchCollection constants = QueueConstant().Matches(source);

            if (constants.Count == 0)
            {
                continue;
            }

            string ns = Namespace().Match(source).Groups[1].Value;
            string type = TypeName().Match(source).Groups[1].Value;

            foreach (Match constant in constants)
            {
                declared[$"{ns}.{type}.{constant.Groups[1].Value}"] = constant.Groups[2].Value;
            }
        }

        declared.ShouldNotBeEmpty("no queue constant was read; the backend's shape has moved");

        return declared;
    }

    [GeneratedRegex(@"public const string (\w*Queue) = ""([a-z0-9-]+)"";")]
    private static partial Regex QueueConstant();

    [GeneratedRegex(@"^namespace ([\w.]+);", RegexOptions.Multiline)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"^(?:public|internal) (?:(?:static|sealed|partial) )*(?:class|record|struct) (\w+)", RegexOptions.Multiline)]
    private static partial Regex TypeName();
}
