using CsvMasker.Web.Jobs;
using CsvMasker.Web.Recipes;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Mvc;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>Progress while queued/running (auto-refresh, no JS), then the verification report and download.</summary>
public sealed class StatusModel(JobRegistry registry, JobRunner runner, TempFileStore store, RecipeStore recipes, ILogger<StatusModel> logger) : JobPageModel(registry)
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
        logger.LogInformation("Job {JobId} cancelled by {User}", Job.Id, Job.Owner);
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
        logger.LogInformation("Job {JobId} downloaded by {User} ({Bytes} bytes)", Job.Id, Job.Owner, stream.Length);
        return File(stream, "text/csv", Job.MaskedFileName);
    }

    [TempData]
    public string? RecipeMessage { get; set; }

    public string? RecipeError { get; private set; }

    /// <summary>The job was pre-filled from a recipe the current user owns, so it can be updated in place.</summary>
    public bool CanUpdateRecipe => Job.Recipe is { } applied && string.Equals(applied.Owner, Job.Owner, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves this job's confirmed strategies (configuration only) as a new recipe.</summary>
    public IActionResult OnPostSaveRecipe(Guid id, string? recipeName)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State != JobState.Completed)
            return RedirectToPage(new { id });

        if (string.IsNullOrWhiteSpace(recipeName))
        {
            RecipeError = "Give the recipe a name.";
            return Page();
        }

        var recipe = recipes.Create(recipeName, Job.Owner, HeaderSignature.Compute(Job.Profile.Header.OriginalNames), RecipeColumns(), Job.SkipMalformed);
        Job.Recipe = new AppliedRecipe(recipe.Id, recipe.Name, recipe.Owner, RecipeMatchKind.Exact, Job.Profile.Columns.Count, Job.Profile.Columns.Count);
        RecipeMessage = $"Saved as recipe \"{recipe.Name}\". Files with these columns will be pre-filled from it.";
        return RedirectToPage(new { id });
    }

    /// <summary>Replaces the recipe this job was pre-filled from with this job's layout and strategies (owner only).</summary>
    public IActionResult OnPostUpdateRecipe(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State != JobState.Completed || Job.Recipe is null)
            return RedirectToPage(new { id });

        var result = recipes.Update(Job.Recipe.Id, Job.Owner, HeaderSignature.Compute(Job.Profile.Header.OriginalNames), RecipeColumns(), Job.SkipMalformed);
        RecipeMessage = result switch
        {
            RecipeChange.Done => $"Updated recipe \"{Job.Recipe.Name}\".",
            RecipeChange.NotOwner => "Only the recipe's owner can update it. Save it as a new recipe instead.",
            _ => "That recipe no longer exists. Save it as a new recipe instead.",
        };
        return RedirectToPage(new { id });
    }

    private List<RecipeColumn> RecipeColumns()
    {
        var header = Job.Profile.Header;
        return header.Keys
            .Select((key, i) => RuleMapper.ToRecipeColumn(header.OriginalNames[i], Job.Plan.Columns[key]))
            .ToList();
    }
}
