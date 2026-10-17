using Admin.Host.Jobs;

namespace Admin.Host.Tests.TestSupport;

/// <summary>
/// Wraps the host's fake runner to fail the commands <see cref="FailIf"/> picks, which FakePlatform's scripts cannot
/// do: every command they script succeeds. Set <see cref="FailIf"/> after a request that must succeed has been made.
/// </summary>
public sealed class FailingProcessRunner(IProcessRunner inner, JobRegistry registry) : IProcessRunner
{
    public Func<ProcessSpec, bool>? FailIf { get; set; }

    public Job Start(ProcessSpec spec)
    {
        if (FailIf?.Invoke(spec) != true)
        {
            return inner.Start(spec);
        }

        Job failed = registry.Create(spec);
        failed.Append(OutputStream.Stderr, "fake: forced failure");
        failed.MarkExited(1);

        return failed;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken) => inner.StopAsync(job, cancellationToken);
}
