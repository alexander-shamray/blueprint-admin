using System.Collections.Concurrent;

namespace Admin.Host.Jobs;

/// <summary>
/// Every job the host has started, in memory for the host's lifetime, exited ones trimmed past the last fifty.
/// The trim runs whenever a job is created or exits, so a host that stops creating jobs still lets go of old ones.
/// </summary>
public sealed class JobRegistry(TimeProvider time)
{
    public const int KeepExited = 50;

    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly Lock trimGate = new();

    public Job Create(ProcessSpec spec)
    {
        Job job = new(Guid.CreateVersion7().ToString("N"), spec, time);
        jobs[job.Id] = job;
        _ = job.Completion.ContinueWith(_ => Trim(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Trim();

        return job;
    }

    public Job? Find(string id) => jobs.GetValueOrDefault(id);

    public IReadOnlyList<Job> All() => jobs.Values.OrderBy(j => j.StartedAt).ToList();

    // Serialised: completions and creates call this from any thread, and two
    // overlapping snapshots must not each remove a different "oldest" job.
    private void Trim()
    {
        lock (trimGate)
        {
            List<Job> exited = jobs.Values.Where(j => j.State == JobState.Exited).OrderBy(j => j.StartedAt).ToList();

            foreach (Job stale in exited.Take(Math.Max(0, exited.Count - KeepExited)))
            {
                jobs.TryRemove(stale.Id, out _);
            }
        }
    }
}
