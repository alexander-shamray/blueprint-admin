using Admin.Host.Jobs;

namespace Admin.Host.Transcript;

/// <summary>
/// Keeps each process the operator caused in the <see cref="OperatorTranscript"/>, and leaves out the reads a
/// screen polls: the line is <see cref="ProcessSpec.Listed"/>, the one <c>/api/jobs</c> already draws (spec §5.2).
/// Every job is the inner runner's own, returned as it started it.
/// </summary>
public sealed class TranscribingProcessRunner(IProcessRunner inner, OperatorTranscript transcript) : IProcessRunner
{
    /// <summary>The runner this one wraps; for tests that ask which runner the options chose.</summary>
    internal IProcessRunner Inner => inner;

    public Job Start(ProcessSpec spec)
    {
        Job job = inner.Start(spec);

        if (spec.Listed)
        {
            transcript.Process(job);
        }

        return job;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken) => inner.StopAsync(job, cancellationToken);
}
