using System.Text.RegularExpressions;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// <see cref="StressRunService"/> runs run-locally.md's "Stress run (k6)" blocks whole, so each is held to the document whole, in
/// its order; the way back is the section's last block, which must stay the command the Stack's Up runs. The lines the host reads,
/// the codes it names and the minute it waits are held to the same section, the scripts the run names to the clone, and the
/// services the override changes to the override file itself.
/// </summary>
public sealed partial class StressRunDriftTests
{
    private const string Section = "## Stress run (k6)";

    [Fact]
    public void Every_block_the_stress_run_runs_is_a_powershell_block_of_the_section_word_for_word_in_its_order()
    {
        string section = SectionOf(Document());
        string[] blocks = PowerShellBlocks(section);
        string[] run = [.. StressRunService.Steps.Skip(1).Where(s => s.Name != StressRunService.SettleStep).Select(s => s.Block)];

        run.ShouldBe(blocks[..run.Length]);
        Document().IndexOf(LoadRunService.K6Read, StringComparison.Ordinal).ShouldBeLessThan(Document().IndexOf(Section, StringComparison.Ordinal));
    }

    [Fact]
    public void The_way_back_is_the_sections_last_block_and_the_command_the_stacks_up_runs()
    {
        string[] blocks = PowerShellBlocks(SectionOf(Document()));
        FakeTimeProvider time = new();
        FakeProcessRunner runner = new(new JobRegistry(time));
        RepoPaths paths = new("blueprint-backend", "blueprint-frontend", new AdminOptions().ComposeFile);

        new ComposeService(runner, paths, time).Up();
        ProcessSpec up = runner.Started.Single();

        // The up, Prometheus's read and the run are the job's; the one after them is the way back, and the last. The Up
        // asks Compose for its JSON progress, which changes how Compose reports and not what it runs, so that flag is not
        // the document's.
        string[] arguments = [.. up.Arguments];
        int progress = arguments.AsSpan().IndexOf(ComposeService.Progress.AsSpan());
        progress.ShouldBeGreaterThan(0);
        blocks.Length.ShouldBe(4);
        string.Join(' ', [up.FileName, .. arguments[..progress], .. arguments[(progress + ComposeService.Progress.Length)..]]).ShouldBe(blocks[^1]);
    }

    [Fact]
    public void The_lines_the_host_reads_the_codes_it_names_and_the_minute_it_waits_are_the_sections()
    {
        string section = SectionOf(Document());

        section.ShouldContain($"`{StressRunService.KneePrefix}step 4 (200 VUs): …`");
        section.ShouldContain($"`{StressRunService.SummaryPrefix}artifacts\\stress\\<run>\\summary.md`");
        section.ReplaceLineEndings(" ").ShouldContain("Leave the stack idle for a minute afterwards");
        StressRunService.Settle.ShouldBe(TimeSpan.FromMinutes(1));
        section.ShouldContain(LoadRunService.PrometheusYes);
        StressRunService.Run.ShouldContain($"\"{StressRunService.K6ExitPrefix}$code\"");

        foreach (int code in StressRunService.Verdicts.Keys)
        {
            Regex.IsMatch(section, $@"\*\*{code}\*\*").ShouldBeTrue($"run-locally.md no longer says what k6's {code} means");
        }
    }

    [Fact]
    public void The_variables_the_levels_and_the_rules_and_the_open_levels_cap_are_the_sections_and_the_ending_lines_are_the_ones_the_run_prints()
    {
        string section = SectionOf(Document());
        string flat = Regex.Replace(section, @"\s+", " ");
        string profiles = flat.Split("`$env:STRESS_PROFILE = 'low'`", 2)[1].Split("A level the cap", 2)[0];

        foreach (string level in StressChoice.Levels)
        {
            profiles.ShouldContain(level);
        }

        section.ShouldContain($"`$env:STRESS_STOP = '{StressChoice.Stops[1]}'`");
        section.ShouldContain($"Unset, it is `{StressChoice.Stops[0]}`");
        section.ShouldContain($"`$env:STRESS_MAX_USERS = '{StressChoice.OpenLevelCap}'`");
        StressChoice.Levels.ShouldContain(StressChoice.OpenLevel);
        flat.ShouldContain($"`{StressChoice.OpenLevel}` needs");
        section.ShouldContain($"`{StressRunService.EndedPrefix}knee at step 4 (800 VUs)");

        string readme = File.ReadAllText(Path.Combine(Backend.Dir, "deploy", "observability", "README.md")).ReplaceLineEndings("\n");

        readme.ShouldContain($"`{StressRunService.FirstToGiveUpPrefix}Ordering.Api:");
    }

    [Fact]
    public void The_steps_the_fake_climbs_on_each_level_are_the_ones_the_section_gives()
    {
        string flat = Regex.Replace(SectionOf(Document()), @"\s+", " ");

        StressRunRecordings.Levels.Keys.ShouldBe(StressChoice.Levels, ignoreOrder: true);

        foreach ((string level, (int[] steps, _)) in StressRunRecordings.Levels)
        {
            flat.ShouldContain(level == StressChoice.OpenLevel ? $"{level} from {steps[0]}" : $"{level} {string.Join(", ", steps)}");
        }
    }

    [Fact]
    public void The_scripts_the_run_names_are_in_the_clone_and_the_override_is_the_file_the_up_adds()
    {
        string[] scripts = [.. ScriptPath().Matches(StressRunService.Run).Select(m => m.Value).Distinct()];

        scripts.ShouldBe(["deploy/observability/stress/sample.mjs", "deploy/observability/stress/stress.js", "deploy/observability/stress/summarise.mjs"], ignoreOrder: true);
        scripts.ShouldAllBe(s => File.Exists(Path.Combine(Backend.Dir, s)));
        StressRunService.Up.ShouldContain($"deploy/compose/{StressRunService.OverrideFile}");
        File.Exists(Path.Combine(Backend.Dir, "deploy", "compose", StressRunService.OverrideFile)).ShouldBeTrue();
    }

    [Fact]
    public void The_services_the_override_changes_are_the_ones_its_file_names()
    {
        string overlay = File.ReadAllText(Path.Combine(Backend.Dir, "deploy", "compose", StressRunService.OverrideFile)).ReplaceLineEndings("\n");
        string[] services = [.. OverlayService().Matches(overlay[overlay.IndexOf("\nservices:\n", StringComparison.Ordinal)..]).Select(m => m.Groups[1].Value)];

        services.ShouldBe(StressRunService.OverrideServices, ignoreOrder: true);
    }

    [Fact]
    public void The_service_the_fakes_take_the_host_port_from_is_the_one_whose_ports_the_override_resets_at_its_replica_count()
    {
        string overlay = File.ReadAllText(Path.Combine(Backend.Dir, "deploy", "compose", StressRunService.OverrideFile)).ReplaceLineEndings("\n");
        string body = overlay[overlay.IndexOf("\nservices:\n", StringComparison.Ordinal)..];
        Match[] heads = [.. OverlayService().Matches(body).Cast<Match>()];
        (string Name, string Block)[] reset = [.. heads
            .Select((head, i) => (Name: head.Groups[1].Value, Block: body[head.Index..(i + 1 < heads.Length ? heads[i + 1].Index : body.Length)]))
            .Where(s => s.Block.Contains("ports: !reset []", StringComparison.Ordinal))];

        reset.Select(s => s.Name).ShouldBe([FakePlatformScripts.UnpublishedOnOverride]);
        reset[0].Block.ShouldContain($"replicas: {FakePlatformScripts.ReplicasOnOverride}\n");
    }

    /// <summary>The section from its heading to the next one at the same level.</summary>
    private static string SectionOf(string document)
    {
        int start = document.IndexOf(Section, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"run-locally.md has no {Section}");
        int end = document.IndexOf("\n## ", start + Section.Length, StringComparison.Ordinal);

        return document[start..(end < 0 ? document.Length : end)];
    }

    /// <summary>run-locally.md one line to a newline, as <see cref="StressRunService"/> holds its blocks.</summary>
    private static string Document() => File.ReadAllText(Backend.RunLocally).ReplaceLineEndings("\n");

    private static string[] PowerShellBlocks(string text) =>
        [.. Fence().Matches(text).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"```powershell\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"deploy/observability/\S+?\.m?js")]
    private static partial Regex ScriptPath();

    /// <summary>A service under the file's top-level <c>services:</c>, indented two spaces.</summary>
    [GeneratedRegex(@"^  ([a-z][a-z0-9-]*):$", RegexOptions.Multiline)]
    private static partial Regex OverlayService();
}
