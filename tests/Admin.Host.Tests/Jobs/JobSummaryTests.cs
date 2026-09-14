using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobSummaryTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "ps"], "/tmp");

    [Fact]
    public async Task Of_never_reports_Running_with_an_exit_code_or_Exited_without_one()
    {
        for (int i = 0; i < 10_000; i++)
        {
            Job job = new($"job-{i}", Spec, new FakeTimeProvider());

            Task writer = Task.Run(() => job.MarkExited(7), TestContext.Current.CancellationToken);
            JobSummary summary = JobSummary.Of(job);
            await writer.WaitAsync(TestContext.Current.CancellationToken);

            bool consistent = summary.State == JobState.Running ? summary.ExitCode is null : summary.ExitCode == 7;
            consistent.ShouldBeTrue($"iteration {i}: State={summary.State} ExitCode={summary.ExitCode}");
        }
    }
}
