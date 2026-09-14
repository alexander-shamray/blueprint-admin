namespace Admin.Host.Jobs;

/// <summary>The wire shape of a job: everything but its lines.</summary>
public sealed record JobSummary(string Id, string CommandLine, JobState State, int? ExitCode, DateTimeOffset StartedAt)
{
    public static JobSummary Of(Job job) => new(job.Id, job.Spec.CommandLine, job.State, job.ExitCode, job.StartedAt);
}
