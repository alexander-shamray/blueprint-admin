namespace Admin.Host.Jobs;

public interface IProcessRunner
{
    /// <summary>Start the process and return its job immediately. A process that cannot start is a job that has already exited with -1.</summary>
    Job Start(ProcessSpec spec);

    /// <summary>
    /// Kill the process and its whole tree, then wait for the job to exit: at most
    /// 10 s after the kill, after which the job is marked exited with -1. A job
    /// that has exited is a no-op.
    /// </summary>
    Task StopAsync(Job job, CancellationToken cancellationToken);
}
