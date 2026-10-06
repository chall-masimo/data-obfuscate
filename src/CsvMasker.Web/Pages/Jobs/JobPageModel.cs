using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>Base for job pages: loads the job for the current user only (anyone else's job is a 404).</summary>
public abstract class JobPageModel(JobRegistry registry) : PageModel
{
    protected JobRegistry Registry => registry;

    public Job Job { get; private set; } = null!;

    protected bool TryLoad(Guid id)
    {
        var job = registry.Find(id, UserKey.Of(HttpContext));
        if (job is null)
            return false;
        Job = job;
        registry.Touch(job);
        return true;
    }

    /// <summary>Sends the user to the page for the job's current step.</summary>
    protected IActionResult RedirectToStep() => RedirectToPage(JobSteps.PageFor(Job), new { id = Job.Id });
}
