using System.Collections.Concurrent;

namespace Admin.Host.Jobs;

/// <summary>Every job the host has started, in memory for the host's lifetime, exited ones trimmed past the last fifty.</summary>
public sealed class JobRegistry(TimeProvider time)
{
    public const int KeepExited = 50;

    private readonly ConcurrentDictionary<string, Job> jobs = new();

    public Job Create(ProcessSpec spec)
    {
        Job job = new(Guid.CreateVersion7().ToString("N"), spec, time);
        jobs[job.Id] = job;
        Trim();

        return job;
    }

    public Job? Find(string id) => jobs.GetValueOrDefault(id);

    public IReadOnlyList<Job> All() => jobs.Values.OrderBy(j => j.StartedAt).ToList();

    private void Trim()
    {
        List<Job> exited = jobs.Values.Where(j => j.State == JobState.Exited).OrderBy(j => j.StartedAt).ToList();

        foreach (Job stale in exited.Take(Math.Max(0, exited.Count - KeepExited)))
        {
            jobs.TryRemove(stale.Id, out _);
        }
    }
}
