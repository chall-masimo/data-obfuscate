using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Recipes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CsvMasker.Web.Pages.Recipes;

/// <summary>All saved recipes (team-wide). Only a recipe's owner can delete it.</summary>
public sealed class IndexModel(RecipeStore recipes) : PageModel
{
    public IReadOnlyList<Recipe> Recipes { get; private set; } = [];

    public string CurrentUser => UserKey.Of(HttpContext);

    [TempData]
    public string? Message { get; set; }

    public void OnGet() =>
        Recipes = recipes.List().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IActionResult OnPostDelete(Guid recipeId)
    {
        switch (recipes.Delete(recipeId, CurrentUser))
        {
            case RecipeChange.NotOwner:
                return Forbid();
            case RecipeChange.Done:
                Message = "Recipe deleted.";
                break;
        }
        return RedirectToPage();
    }
}
