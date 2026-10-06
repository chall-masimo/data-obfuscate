using CsvMasker.Web.Storage;

namespace CsvMasker.Web.Jobs;

/// <summary>
/// In-memory jobs. Enforces one open job per user and ownership on every lookup: another user's
/// job id behaves exactly like an unknown one.
/// </summary>
public sealed class JobRegistry(TempFileStore store, TimeProvider time)
{
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly Lock _lock = new();

    public Job? Find(Guid id, string owner)
    {
        lock (_lock)
            return _jobs.TryGetValue(id, out var job) && job.Owner == owner && !job.Removed ? job : null;
    }

    /// <summary>The user's current job (open or finished but not yet discarded), if any.</summary>
    public Job? Current(string owner)
    {
        lock (_lock)
            return _jobs.Values.Where(j => j.Owner == owner).MaxBy(j => j.Created);
    }

    public bool HasOpenJob(string owner)
    {
        lock (_lock)
            return _jobs.Values.Any(j => j.Owner == owner && j.IsOpen);
    }

    /// <summary>Adds a job unless the owner already has an open one. Finished jobs of the same owner are dropped.</summary>
    public bool TryAdd(Job job)
    {
        List<Job> finished;
        lock (_lock)
        {
            if (_jobs.Values.Any(j => j.Owner == job.Owner && j.IsOpen))
                return false;
            finished = _jobs.Values.Where(j => j.Owner == job.Owner).ToList();
            _jobs[job.Id] = job;
        }
        foreach (var old in finished)
            Remove(old);
        return true;
    }

    public IReadOnlyList<Job> All()
    {
        lock (_lock)
            return _jobs.Values.ToList();
    }

    public void Touch(Job job) => job.LastActivity = time.GetUtcNow();

    /// <summary>
    /// Discards a job: cancels it, deletes its files and disposes its session (zeroing the key).
    /// A running job is cleaned up by the runner when it stops.
    /// </summary>
    public void Remove(Job job)
    {
        lock (_lock)
            _jobs.Remove(job.Id);

        bool running;
        lock (job.Sync)
        {
            job.Removed = true;
            running = job.State == JobState.Running;
            if (job.State == JobState.Queued)
                job.State = JobState.Cancelled;
        }

        job.Cancellation.Cancel();
        if (!running)
            Release(job);
    }

    /// <summary>Deletes a job's files and disposes its session.</summary>
    public void Release(Job job)
    {
        lock (job.Sync)
        {
            store.Delete(job.UploadPath);
            store.Delete(job.OutputPath);
            job.UploadPath = null;
            job.OutputPath = null;
            job.Session?.Dispose();
        }
    }
}
