using CsvMasker.Web.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>Cancels the job if needed, deletes its files and forgets it.</summary>
public sealed class DiscardModel(JobRegistry registry, ILogger<DiscardModel> logger) : JobPageModel(registry)
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public IActionResult OnPost(Guid id)
    {
        if (TryLoad(id))
        {
            Registry.Remove(Job);
            logger.LogInformation("Job {JobId} discarded by {User}", Job.Id, Job.Owner);
        }
        return RedirectToPage("/Index");
    }
}
