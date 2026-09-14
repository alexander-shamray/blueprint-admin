using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobRegistryTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "ps"], "/tmp");

    [Fact]
    public void Create_registers_a_running_job_with_a_unique_id()
    {
        JobRegistry registry = new(new FakeTimeProvider());

        Job first = registry.Create(Spec);
        Job second = registry.Create(Spec);

        first.Id.ShouldNotBe(second.Id);
        first.State.ShouldBe(JobState.Running);
        registry.Find(first.Id).ShouldBeSameAs(first);
        registry.All().Count.ShouldBe(2);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_id()
    {
        JobRegistry registry = new(new FakeTimeProvider());

        registry.Find("nope").ShouldBeNull();
    }

    [Fact]
    public void Exited_jobs_beyond_the_keep_limit_are_dropped_oldest_first()
    {
        FakeTimeProvider time = new();
        JobRegistry registry = new(time);
        List<Job> jobs = [];

        for (int i = 0; i < JobRegistry.KeepExited + 2; i++)
        {
            Job job = registry.Create(Spec);
            job.MarkExited(0);
            jobs.Add(job);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Job running = registry.Create(Spec);

        registry.Find(jobs[0].Id).ShouldBeNull();
        registry.Find(jobs[1].Id).ShouldBeNull();
        registry.Find(jobs[2].Id).ShouldNotBeNull();
        registry.Find(running.Id).ShouldNotBeNull();
        registry.All().Count.ShouldBe(JobRegistry.KeepExited + 1);
    }
}
