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

    private static readonly InstallReading Current = new(InstallState.Current, "node_modules matches package-lock.json.");
    private static readonly InstallReading Missing = new(InstallState.Missing, "/repo/frontend has no node_modules. Run npm ci there; the console does not.");
    private static readonly InstallReading Stale = new(InstallState.Stale, "node_modules does not match package-lock.json. Run npm ci in /repo/frontend; the console does not.");

    private FrontendSupervisor Supervisor(bool installed = true) => new(runner, Paths, _ => installed ? Current : Missing);

    private FrontendSupervisor Supervisor(InstallReading reading) => new(runner, Paths, _ => reading);

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
    public async Task The_device_build_runs_the_emulator_script_beside_a_running_dev_server()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        runner.OnLongRunning("npm", "run emulator:android", "> cap sync android");
        using FrontendSupervisor supervisor = Supervisor();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        FrontendStartResult build = await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken);

        build.Outcome.ShouldBe(FrontendStartOutcome.Started);
        ProcessSpec spec = runner.Started[^1];
        spec.Arguments.ShouldBe(["run", "emulator:android"]);
        spec.WorkingDirectory.ShouldBe("/repo/frontend");
        supervisor.Status().DeviceJob!.Id.ShouldBe(build.Job!.Id);
        supervisor.Status().Job!.Id.ShouldNotBe(build.Job.Id);
    }

    [Fact]
    public async Task A_second_device_build_while_one_runs_starts_nothing()
    {
        runner.OnLongRunning("npm", "run emulator:android", "> cap sync android");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult first = await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken);

        FrontendStartResult second = await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken);

        second.Outcome.ShouldBe(FrontendStartOutcome.AlreadyRunning);
        second.Job.ShouldBeSameAs(first.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_finished_device_build_can_be_run_again_and_without_node_modules_none_starts()
    {
        runner.On("npm", "run emulator:android", 0, "[info] Sync finished");
        using FrontendSupervisor supervisor = Supervisor();
        await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken);

        (await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken)).Outcome.ShouldBe(FrontendStartOutcome.Started);
        runner.Started.Count.ShouldBe(2);

        using FrontendSupervisor missing = Supervisor(installed: false);
        (await missing.StartDeviceBuildAsync(TestContext.Current.CancellationToken)).Outcome.ShouldBe(FrontendStartOutcome.NotInstalled);
        runner.Started.Count.ShouldBe(2);
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

        installed.Status().ShouldBe(new FrontendStatus(null, true, null));
        missing.Status().ShouldBe(new FrontendStatus(null, false, null, Missing.Detail));
    }

    [Fact]
    public async Task A_stale_install_is_refused_by_Start_and_by_the_device_build_with_the_reason()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor(Stale);

        FrontendStartResult start = await supervisor.StartAsync(TestContext.Current.CancellationToken);
        FrontendStartResult device = await supervisor.StartDeviceBuildAsync(TestContext.Current.CancellationToken);

        start.ShouldBe(new FrontendStartResult(FrontendStartOutcome.NotInstalled, null, Stale.Detail));
        device.ShouldBe(new FrontendStartResult(FrontendStartOutcome.NotInstalled, null, Stale.Detail));
        runner.Started.ShouldBeEmpty();
        supervisor.Status().ShouldBe(new FrontendStatus(null, false, null, Stale.Detail));
    }

    [Fact]
    public async Task An_install_that_cannot_be_compared_with_its_lock_does_not_block_Start()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor(new InstallReading(InstallState.Unverified, "package-lock.json is not there."));

        (await supervisor.StartAsync(TestContext.Current.CancellationToken)).Outcome.ShouldBe(FrontendStartOutcome.Started);
        supervisor.Status().InstallProblem.ShouldBeNull();
    }
}
