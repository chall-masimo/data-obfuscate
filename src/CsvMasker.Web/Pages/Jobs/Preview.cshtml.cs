using CsvMasker.Core.Masking;
using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Mvc;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>The first 20 rows, original vs masked, using the same key as the real run.</summary>
public sealed class PreviewModel(JobRegistry registry, JobRunner runner, TempFileStore store, ILogger<PreviewModel> logger) : JobPageModel(registry)
{
    public const int Rows = 20;

    public IReadOnlyList<PreviewRow> Preview { get; private set; } = [];

    public string? Error { get; private set; }

    public IActionResult OnGet(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State != JobState.Reviewed || Job.Session is null || Job.UploadPath is null)
            return RedirectToStep();

        try
        {
            using var stream = store.OpenRead(Job.UploadPath);
            Preview = Job.Session.Preview(stream, Job.Dialect, Job.Plan, Rows);
        }
        catch (Exception ex)
        {
            Error = SafeErrors.ForUser(ex);
            logger.LogInformation("Job {JobId} preview failed: {Error}", Job.Id, SafeErrors.ForLog(ex));
        }
        return Page();
    }

    public IActionResult OnPostRun(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (runner.Enqueue(Job))
            logger.LogInformation("Job {JobId} queued", Job.Id);
        return RedirectToPage("/Jobs/Status", new { id });
    }
}
