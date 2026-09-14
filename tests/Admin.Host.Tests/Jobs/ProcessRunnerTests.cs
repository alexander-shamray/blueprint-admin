using Admin.Host.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class ProcessRunnerTests
{
    private readonly JobRegistry registry = new(TimeProvider.System);

    private ProcessRunner Runner => new(registry, NullLogger<ProcessRunner>.Instance);

    [Fact]
    public async Task Captures_stdout_and_stderr_and_the_exit_code()
    {
        ProcessSpec spec = new("node", ["-e", "console.log('out'); console.error('err'); process.exit(3)"], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        exit.ShouldBe(3);
        job.State.ShouldBe(JobState.Exited);
        job.Since(-1).Select(l => (l.Stream, l.Text)).ShouldBe([(OutputStream.Stdout, "out"), (OutputStream.Stderr, "err")], ignoreOrder: true);
        registry.Find(job.Id).ShouldBeSameAs(job);
    }

    [Fact]
    public async Task Stop_kills_a_long_running_process()
    {
        ProcessSpec spec = new("node", ["-e", "setInterval(() => console.log('tick'), 50)"], Environment.CurrentDirectory);
        ProcessRunner runner = Runner;

        Job job = runner.Start(spec);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Running);
        job.Since(-1).ShouldNotBeEmpty();

        await runner.StopAsync(job, TestContext.Current.CancellationToken);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
        job.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task Disposing_the_runner_kills_a_long_running_child_and_its_job_exits()
    {
        ProcessSpec spec = new("node", ["-e", "setInterval(() => console.log('tick'), 50)"], Environment.CurrentDirectory);
        ProcessRunner runner = Runner;

        Job job = runner.Start(spec);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Running);

        await runner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task A_missing_executable_is_an_exited_job_not_an_exception()
    {
        ProcessSpec spec = new("definitely-not-on-path-4f2c", [], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(-1);
        job.Since(-1).Single().Stream.ShouldBe(OutputStream.Stderr);
        job.Since(-1).Single().Text.ShouldContain("definitely-not-on-path-4f2c");
    }

    [Fact]
    public async Task A_fast_exiting_process_loses_none_of_its_output_lines()
    {
        string script = string.Join("; ", Enumerable.Range(0, 200).Select(i => $"console.log({i})"));
        ProcessSpec spec = new("node", ["-e", script], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        job.Since(-1).Select(l => l.Text).ShouldBe(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Stop_on_a_process_racing_its_own_fast_exit_does_not_throw()
    {
        ProcessSpec spec = new("node", ["-e", "process.exit(0)"], Environment.CurrentDirectory);
        ProcessRunner runner = Runner;

        Job job = runner.Start(spec);

        // No delay: StopAsync may run concurrently with (or after) the
        // runner's own completion handling, which disposes the process.
        await runner.StopAsync(job, TestContext.Current.CancellationToken);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task Npm_starts_by_its_bare_name_on_every_platform()
    {
        // On Windows npm is npm.cmd, and CreateProcess only appends .exe; the
        // frontend supervisor starts "npm start" by name (spec §2.1).
        ProcessSpec spec = new("npm", ["--version"], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, string.Join(Environment.NewLine, job.Since(-1).Select(l => l.Text)));
        job.Since(-1).ShouldContain(l => l.Stream == OutputStream.Stdout && l.Text.Length > 0);
    }
}
