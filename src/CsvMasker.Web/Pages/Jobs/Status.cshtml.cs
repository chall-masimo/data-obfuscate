using CsvMasker.Web.Jobs;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Mvc;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>Progress while queued/running (auto-refresh, no JS), then the verification report and download.</summary>
public sealed class StatusModel(JobRegistry registry, JobRunner runner, TempFileStore store, ILogger<StatusModel> logger) : JobPageModel(registry)
{
    public const int RefreshSeconds = 2;

    public bool InProgress => Job.State is JobState.Queued or JobState.Running;

    public IActionResult OnGet(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is JobState.Uploaded or JobState.Reviewed)
            return RedirectToStep();
        return Page();
    }

    public IActionResult OnPostCancel(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        runner.Cancel(Job);
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Streams the masked file and deletes it as soon as the response finishes. A POST, so link
    /// prefetching can never consume the one-time download.
    /// </summary>
    public IActionResult OnPostDownload(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();

        string path;
        lock (Job.Sync)
        {
            if (Job.State != JobState.Completed || Job.Downloaded || Job.OutputPath is null)
                return RedirectToPage(new { id });
            path = Job.OutputPath;
            Job.OutputPath = null;
            Job.Downloaded = true;
        }

        var stream = store.OpenReadAndDeleteOnClose(path);
        logger.LogInformation("Job {JobId} downloaded ({Bytes} bytes)", Job.Id, stream.Length);
        return File(stream, "text/csv", Job.MaskedFileName);
    }
}
