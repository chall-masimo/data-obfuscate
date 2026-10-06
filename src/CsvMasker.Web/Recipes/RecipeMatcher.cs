using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Web.Recipes;

public enum RecipeMatchKind
{
    Exact,
    Partial,
}

/// <summary>How well a recipe fits a file: matched columns out of the file's total.</summary>
public sealed record RecipeMatch(Recipe Recipe, RecipeMatchKind Kind, int Matched, int Total)
{
    public double Score => Total == 0 ? 0 : (double)Matched / Total;
}

/// <summary>
/// Finds the recipe to pre-fill a file's review with. An exact header signature wins.
/// Otherwise the recipe sharing the most column names, if it covers at least half the file's
/// columns. Ties go to the most recently updated recipe.
/// </summary>
public static class RecipeMatcher
{
    public const double MinimumPartialScore = 0.5;

    /// <summary>Every recipe scored against the file, best first.</summary>
    public static IReadOnlyList<RecipeMatch> Rank(IReadOnlyList<string> fileNames, IEnumerable<Recipe> recipes)
    {
        string signature = HeaderSignature.Compute(fileNames);
        return recipes
            .Select(recipe => recipe.HeaderSignature == signature
                ? new RecipeMatch(recipe, RecipeMatchKind.Exact, fileNames.Count, fileNames.Count)
                : new RecipeMatch(recipe, RecipeMatchKind.Partial, Align(fileNames, recipe).Count, fileNames.Count))
            .OrderBy(m => m.Kind)
            .ThenByDescending(m => m.Score)
            .ThenByDescending(m => m.Recipe.Updated)
            .ToList();
    }

    public static RecipeMatch? Best(IReadOnlyList<string> fileNames, IEnumerable<Recipe> recipes) =>
        Rank(fileNames, recipes).FirstOrDefault(m => m.Kind == RecipeMatchKind.Exact || m.Score >= MinimumPartialScore);

    /// <summary>
    /// File column index → recipe column, by normalized name. The nth occurrence of a duplicate
    /// name in the file matches the nth occurrence in the recipe.
    /// </summary>
    public static Dictionary<int, RecipeColumn> Align(IReadOnlyList<string> fileNames, Recipe recipe)
    {
        var byName = recipe.Columns
            .GroupBy(c => HeaderSignature.Normalize(c.Name))
            .ToDictionary(g => g.Key, g => new Queue<RecipeColumn>(g));

        var aligned = new Dictionary<int, RecipeColumn>();
        for (int i = 0; i < fileNames.Count; i++)
            if (byName.TryGetValue(HeaderSignature.Normalize(fileNames[i]), out var queue) && queue.Count > 0)
                aligned[i] = queue.Dequeue();
        return aligned;
    }

    /// <summary>
    /// A plan with rules for the columns the recipe covers. Columns it doesn't cover are left
    /// out, so they show as unassigned and block the run until chosen.
    /// </summary>
    public static MaskingPlan Apply(FileProfile profile, Recipe recipe)
    {
        var names = profile.Header.OriginalNames;
        var rules = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
        foreach (var (index, column) in Align(names, recipe))
            rules[profile.Columns[index].Key] = RuleMapper.ToRule(column, profile.Columns[index]);
        return new MaskingPlan(rules);
    }
}
