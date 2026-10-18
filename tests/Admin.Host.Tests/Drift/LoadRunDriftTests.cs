using System.Text.RegularExpressions;
using Admin.Host.Stack;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="LoadRunService"/> runs run-locally.md's "Load run (k6)" blocks whole, so each is held to the document whole: a block
/// that differs by a character is a block the document does not have. The lines the host reads off their output and the k6 codes
/// it names are held to the same section, and the script the run names to the clone.
/// </summary>
public sealed partial class LoadRunDriftTests
{
    private const string Section = "## Load run (k6)";

    [Fact]
    public void Every_block_the_load_run_runs_is_a_powershell_block_of_the_document_word_for_word()
    {
        string[] blocks = PowerShellBlocks(Document());

        LoadRunService.Steps.Select(s => s.Block).Where(b => !blocks.Contains(b)).ShouldBeEmpty();
    }

    [Fact]
    public void The_steps_are_in_the_documents_order_with_the_k6_read_of_step_0_first()
    {
        string document = Document();
        string section = SectionOf(document);
        int[] at = [.. LoadRunService.Steps.Skip(1).Select(s => section.IndexOf(s.Block, StringComparison.Ordinal))];

        at.ShouldAllBe(i => i >= 0);
        at.ShouldBe(at.Order());
        document.IndexOf(LoadRunService.K6Read, StringComparison.Ordinal).ShouldBeLessThan(document.IndexOf(Section, StringComparison.Ordinal));
    }

    [Fact]
    public void The_lines_the_host_reads_and_the_codes_it_names_are_the_documents()
    {
        string document = Document();
        string section = SectionOf(document);

        document.ShouldContain(LoadRunService.K6Missing);
        section.ShouldContain(LoadRunService.PrometheusYes);
        section.ShouldContain("exit 0");

        foreach (int code in LoadRunService.Verdicts.Keys.Where(c => c != 0))
        {
            Regex.IsMatch(section, $@"\b{code}\b").ShouldBeTrue($"run-locally.md no longer says what k6's {code} means");
        }
    }

    [Fact]
    public void The_script_the_run_names_is_in_the_clone()
    {
        string script = ScriptPath().Match(LoadRunService.Run).Value;

        script.ShouldBe("deploy/observability/slo/slo.js");
        File.Exists(Path.Combine(Backend.Dir, script)).ShouldBeTrue();
    }

    /// <summary>The section from its heading to the next one at the same level.</summary>
    private static string SectionOf(string document)
    {
        int start = document.IndexOf(Section, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"run-locally.md has no {Section}");
        int end = document.IndexOf("\n## ", start + Section.Length, StringComparison.Ordinal);

        return document[start..(end < 0 ? document.Length : end)];
    }

    /// <summary>run-locally.md one line to a newline, as <see cref="LoadRunService"/> holds its blocks.</summary>
    private static string Document() => File.ReadAllText(Backend.RunLocally).ReplaceLineEndings("\n");

    private static string[] PowerShellBlocks(string document) =>
        [.. Fence().Matches(document).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"```powershell\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"deploy/\S+\.js")]
    private static partial Regex ScriptPath();
}
