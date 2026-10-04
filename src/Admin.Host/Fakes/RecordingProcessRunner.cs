using System.Collections.Concurrent;
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// The process runner's recording seam: a Compose read <see cref="FixtureRecordings.Process"/> names has its
/// standard output written to the fixture once it exits 0. Every job, recorded or not, is the inner runner's own,
/// returned as it started it.
/// </summary>
public sealed class RecordingProcessRunner(IProcessRunner inner, FixtureRecorder recorder, RepoPaths paths) : IProcessRunner
{
    private readonly ConcurrentDictionary<Task, byte> recording = new();

    public Job Start(ProcessSpec spec)
    {
        Job job = inner.Start(spec);

        if (recorder.On && FixtureRecordings.For(paths, spec) is { } fixture)
        {
            Task written = RecordAsync(job, fixture.Fixture);
            recording.TryAdd(written, 0);
            _ = written.ContinueWith(t => recording.TryRemove(t, out _), TaskScheduler.Default);
        }

        return job;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken) => inner.StopAsync(job, cancellationToken);

    /// <summary>Every recording still being written; for tests, which otherwise race the job's exit.</summary>
    internal Task Settled() => Task.WhenAll(recording.Keys);

    private async Task RecordAsync(Job job, string fixture)
    {
        if (await job.Completion != 0)
        {
            return;
        }

        IReadOnlyList<OutputLine> lines = job.Since(-1);

        // As OneShot reads it: a first kept line past sequence 0 means the ring dropped the start, a leading "[" with it.
        if (lines.Count > 0 && lines[0].Sequence != 0)
        {
            return;
        }

        IEnumerable<string> stdout = lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text);
        recorder.Write(fixture, string.Join('\n', stdout) + "\n");
    }
}
