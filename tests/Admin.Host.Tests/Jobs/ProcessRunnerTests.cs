using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Admin.Host.Jobs;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Stop_kills_a_grandchild_whose_intermediate_parent_has_already_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Orphaned descendants escape a parent-link tree walk on Windows only.");
        ProcessRunner runner = Runner;
        (Job job, int pid) = await StartOrphanedGrandchildAsync(runner);

        try
        {
            await runner.StopAsync(job, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            job.State.ShouldBe(JobState.Exited);
            (await IsRunningAfterAsync(pid, TimeSpan.FromSeconds(5))).ShouldBeFalse();
        }
        finally
        {
            await KillIfRunningAsync(pid);
        }
    }

    [Fact]
    public async Task Disposing_the_runner_kills_a_grandchild_whose_intermediate_parent_has_already_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Orphaned descendants escape a parent-link tree walk on Windows only.");
        ProcessRunner runner = Runner;
        (Job job, int pid) = await StartOrphanedGrandchildAsync(runner);

        try
        {
            await runner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            (await IsRunningAfterAsync(pid, TimeSpan.FromSeconds(5))).ShouldBeFalse();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await KillIfRunningAsync(pid);
        }
    }

    [Fact]
    public async Task Stop_returns_within_its_bound_and_ends_the_job_when_the_kill_leaves_the_output_open()
    {
        // The seam's kill reaches nothing, as a kill that misses an orphan holding
        // the pipes would: the job's output never ends on its own.
        CapturingLogger logger = new();
        ProcessRunner runner = new(registry, logger, TimeSpan.FromMilliseconds(500), _ => { });
        Job job = runner.Start(new ProcessSpec("node", ["-e", "console.log('pid=' + process.pid); setInterval(() => console.log('tick'), 50)"], Environment.CurrentDirectory));
        int? pid = null;

        try
        {
            pid = await WaitForPidAsync(job);
            await runner.StopAsync(job, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            job.Status.ShouldBe((JobState.Exited, -1));
            job.Since(-1).ShouldContain(l => l.Stream == OutputStream.Stderr && l.Text.Contains("Stopped waiting", StringComparison.Ordinal));
            logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning
                && e.Message.Contains("was still open", StringComparison.Ordinal)
                && e.Message.Contains(job.Spec.CommandLine, StringComparison.Ordinal));
        }
        finally
        {
            if (pid is int leftover)
            {
                await KillIfRunningAsync(leftover);
            }
        }
    }

    /// <summary>
    /// The shape `npm start` gives `ng serve`: root, then an intermediate, then a
    /// long-lived grandchild on the inherited pipes; the intermediate and then the
    /// root exit. Detached keeps the grandchild out of the intermediate's own libuv
    /// job, so it outlives the intermediate as the real orphan does. Returns once the
    /// root has exited, with the job still running on the grandchild's output.
    /// </summary>
    private static async Task<(Job Job, int Pid)> StartOrphanedGrandchildAsync(ProcessRunner runner)
    {
        string grandchild = "console.log('pid=' + process.pid); setInterval(() => console.log('tick'), 50)";
        string intermediate = "require('child_process').spawn(process.execPath, ['-e', " + JsonSerializer.Serialize(grandchild) + "], { stdio: 'inherit', detached: true }); setTimeout(() => process.exit(0), 500)";
        string root = "require('child_process').spawn(process.execPath, ['-e', " + JsonSerializer.Serialize(intermediate) + "], { stdio: 'inherit' }).on('exit', () => { console.log('root-exiting'); process.exit(0); })";
        Job job = runner.Start(new ProcessSpec("node", ["-e", root], Environment.CurrentDirectory));

        int pid = await WaitForPidAsync(job);

        try
        {
            await WaitForLineAsync(job, "root-exiting");
            await Task.Delay(500, TestContext.Current.CancellationToken);
            job.State.ShouldBe(JobState.Running);
        }
        catch
        {
            await KillIfRunningAsync(pid);

            throw;
        }

        return (job, pid);
    }

    private static async Task KillIfRunningAsync(int pid)
    {
        if (await IsRunningAfterAsync(pid, TimeSpan.Zero))
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
    }

    private static async Task<int> WaitForPidAsync(Job job)
    {
        string line = await WaitForLineAsync(job, "pid=");

        return int.Parse(line["pid=".Length..], CultureInfo.InvariantCulture);
    }

    private static async Task<string> WaitForLineAsync(Job job, string prefix)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            OutputLine? line = job.Since(-1).FirstOrDefault(l => l.Text.StartsWith(prefix, StringComparison.Ordinal));

            if (line is not null)
            {
                return line.Text;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No line starting '{prefix}': {string.Join(" | ", job.Since(-1).Select(l => l.Text))}");
    }

    /// <summary>Whether the process is still running once <paramref name="grace"/> has passed (it may take a moment to die).</summary>
    private static async Task<bool> IsRunningAfterAsync(int pid, TimeSpan grace)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + grace;

        while (true)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);

                if (process.HasExited)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<ProcessRunner>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
