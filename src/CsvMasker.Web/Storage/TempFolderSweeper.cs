using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Storage;

/// <summary>
/// Deletes temp files and idle jobs older than <see cref="StorageOptions.SweepAgeMinutes"/>:
/// abandoned sessions, failed deletes, and leftovers from an app-pool recycle.
/// </summary>
public sealed class TempFolderSweeper(
    TempFileStore store,
    JobRegistry registry,
    TimeProvider time,
    IOptions<StorageOptions> options,
    ILogger<TempFolderSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                SweepOnce();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Temp folder sweep failed: {ErrorType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <returns>Number of jobs and files removed.</returns>
    public (int Jobs, int Files) SweepOnce()
    {
        var cutoff = time.GetUtcNow().AddMinutes(-options.Value.SweepAgeMinutes);

        int jobs = 0;
        foreach (var job in registry.All())
        {
            if (job.LastActivity < cutoff && job.State != JobState.Running)
            {
                registry.Remove(job);
                jobs++;
            }
        }

        // Files still owned by a live job (e.g. a long-running job's upload) are left alone.
        var inUse = registry.All()
            .SelectMany(j => new[] { j.UploadPath, j.OutputPath })
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int files = 0;
        foreach (var file in store.Files())
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime && !inUse.Contains(file.FullName))
            {
                store.Delete(file.FullName);
                files++;
            }
        }

        if (jobs + files > 0)
            logger.LogInformation("Sweeper removed {Jobs} idle job(s) and {Files} temp file(s)", jobs, files);
        return (jobs, files);
    }
}
