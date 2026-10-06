using System.Diagnostics;
using System.Threading.Channels;
using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Options;
using CsvMasker.Web.Storage;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Jobs;

/// <summary>
/// Runs masking jobs in the background, at most <see cref="LimitsOptions.MaxConcurrentJobs"/> at a
/// time across all users, so a burst of jobs can't starve the server's other tenants.
/// </summary>
public sealed class JobRunner(
    JobRegistry registry,
    TempFileStore store,
    TimeProvider time,
    IOptions<LimitsOptions> limits,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();

    /// <summary>Queues a reviewed job. Returns false if it isn't in a state that can run.</summary>
    public bool Enqueue(Job job)
    {
        lock (job.Sync)
        {
            if (job.State != JobState.Reviewed || job.Session is null || job.Removed)
                return false;
            job.State = JobState.Queued;
        }
        return _queue.Writer.TryWrite(job);
    }

    /// <summary>Cancels a queued or running job; the worker cleans up.</summary>
    public void Cancel(Job job)
    {
        lock (job.Sync)
        {
            if (job.State == JobState.Queued)
            {
                job.State = JobState.Cancelled;
                job.Finished = time.GetUtcNow();
                store.Delete(job.UploadPath);
                job.UploadPath = null;
            }
        }
        job.Cancellation.Cancel();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, limits.Value.MaxConcurrentJobs)
            .Select(_ => Task.Run(() => WorkAsync(stoppingToken), stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                Run(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal void Run(Job job, CancellationToken stoppingToken)
    {
        lock (job.Sync)
        {
            if (job.State != JobState.Queued || job.Removed || job.Session is null || job.UploadPath is null)
                return;
            job.State = JobState.Running;
            job.Started = time.GetUtcNow();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, stoppingToken);
        string outputPath = store.NewPath("output");
        var stopwatch = Stopwatch.StartNew();
        JobState outcome;
        try
        {
            using (var input = store.OpenRead(job.UploadPath))
            using (var output = store.Create(outputPath))
            {
                job.Report = job.Session.Run(input, job.Dialect, output, job.Plan, new RowProgress(job), linked.Token);
            }
            job.OutputPath = outputPath;
            outcome = JobState.Completed;
            logger.LogInformation(
                "Job {JobId} for {User} completed: {Rows} rows, {Columns} columns in {ElapsedMs} ms; verification failures: {HasFailures}",
                job.Id, job.Owner, job.Report.RowsWritten, job.Report.Columns.Count, stopwatch.ElapsedMilliseconds, job.Report.HasFailures);
        }
        catch (OperationCanceledException)
        {
            store.Delete(outputPath);
            outcome = JobState.Cancelled;
            logger.LogInformation("Job {JobId} for {User} cancelled after {Rows} rows", job.Id, job.Owner, job.RowsProcessed);
        }
        catch (Exception ex)
        {
            store.Delete(outputPath);
            job.Error = SafeErrors.ForUser(ex);
            outcome = JobState.Failed;
            logger.LogWarning("Job {JobId} for {User} failed: {Error}", job.Id, job.Owner, SafeErrors.ForLog(ex));
        }

        lock (job.Sync)
        {
            store.Delete(job.UploadPath); // the raw upload goes as soon as the job completes or fails
            job.UploadPath = null;
            job.State = outcome;
            job.Finished = time.GetUtcNow();
            job.LastActivity = time.GetUtcNow();
        }

        if (job.Removed)
            registry.Release(job); // discarded while running
    }

    private sealed class RowProgress(Job job) : IProgress<long>
    {
        public void Report(long value) => job.RowsProcessed = value;
    }
}
