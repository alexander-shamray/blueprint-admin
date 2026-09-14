using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class FrontendSupervisorTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/compose.yml");

    private readonly JobRegistry registry = new(new FakeTimeProvider());
    private readonly FakeProcessRunner runner;

    public FrontendSupervisorTests()
    {
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    private FrontendSupervisor Supervisor(bool installed = true) => new(runner, Paths, _ => installed);

    [Fact]
    public async Task Start_runs_npm_start_in_the_frontend_clone()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendStartResult result = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FrontendStartOutcome.Started);
        result.Job!.State.ShouldBe(JobState.Running);
        ProcessSpec spec = runner.Started.Single();
        spec.FileName.ShouldBe("npm");
        spec.Arguments.ShouldBe(["start"]);
        spec.WorkingDirectory.ShouldBe("/repo/frontend");
    }

    [Fact]
    public async Task Start_refuses_while_a_job_is_running_and_starts_nothing()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult first = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        FrontendStartResult second = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        second.Outcome.ShouldBe(FrontendStartOutcome.AlreadyRunning);
        second.Job.ShouldBeSameAs(first.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_refuses_when_node_modules_is_absent()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor(installed: false);

        FrontendStartResult result = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FrontendStartOutcome.NotInstalled);
        result.Job.ShouldBeNull();
        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_that_exited_on_its_own_can_be_started_again()
    {
        // ng serve exits at once when 5173 is taken; that job is over, not running.
        runner.On("npm", "start", 1, "Port 5173 is already in use.");
        using FrontendSupervisor supervisor = Supervisor();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        FrontendStartResult again = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        again.Outcome.ShouldBe(FrontendStartOutcome.Started);
        runner.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_starts_start_one_process()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendStartResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => supervisor.StartAsync(TestContext.Current.CancellationToken))));

        runner.Started.Count.ShouldBe(1);
        results.Count(r => r.Outcome == FrontendStartOutcome.Started).ShouldBe(1);
    }

    [Fact]
    public async Task Stop_kills_the_running_job_and_status_keeps_its_exit_code()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult started = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Job? stopped = await supervisor.StopAsync(TestContext.Current.CancellationToken);

        stopped.ShouldBeSameAs(started.Job);
        stopped!.State.ShouldBe(JobState.Exited);
        FrontendStatus status = supervisor.Status();
        status.Job!.Id.ShouldBe(started.Job!.Id);
        status.Job.State.ShouldBe(JobState.Exited);
        status.Job.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Stop_with_nothing_running_returns_null()
    {
        using FrontendSupervisor supervisor = Supervisor();

        (await supervisor.StopAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void Status_before_any_start_has_no_job_and_reports_installation()
    {
        using FrontendSupervisor installed = Supervisor(installed: true);
        using FrontendSupervisor missing = Supervisor(installed: false);

        installed.Status().ShouldBe(new FrontendStatus(null, true));
        missing.Status().ShouldBe(new FrontendStatus(null, false));
    }

    [Fact]
    public void HasNodeModules_is_true_only_when_the_directory_exists()
    {
        DirectoryInfo clone = Directory.CreateTempSubdirectory("admin-frontend-");

        try
        {
            FrontendSupervisor.HasNodeModules(clone.FullName).ShouldBeFalse();

            clone.CreateSubdirectory("node_modules");

            FrontendSupervisor.HasNodeModules(clone.FullName).ShouldBeTrue();
        }
        finally
        {
            clone.Delete(recursive: true);
        }
    }
}
