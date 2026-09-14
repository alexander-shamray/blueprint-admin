namespace Admin.Host.Jobs;

/// <summary>The wire shape of a job: everything but its lines.</summary>
public sealed record JobSummary(string Id, string CommandLine, JobState State, int? ExitCode, DateTimeOffset StartedAt)
{
    public static JobSummary Of(Job job)
    {
        (JobState state, int? exitCode) = job.Status;

        return new JobSummary(job.Id, job.Spec.CommandLine, state, exitCode, job.StartedAt);
    }
}
