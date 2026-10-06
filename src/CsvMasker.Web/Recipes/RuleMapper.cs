using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Web.Recipes;

/// <summary>Converts between plan rules and recipe columns.</summary>
public static class RuleMapper
{
    public static RecipeColumn ToRecipeColumn(string name, ColumnRule rule)
    {
        // MappingDomain is stored as a column name; RecipeMatcher.ToRecipe fills it in.
        var column = new RecipeColumn { Name = name, Strategy = rule.Strategy };
        switch (rule.Options)
        {
            case HashIdOptions o:
                column.Prefix = o.Prefix.Length > 0 ? o.Prefix : null;
                break;
            case FakeOptions o:
                column.FakeKind = o.Kind;
                break;
            case PerturbOptions o:
                column.PerturbPercent = (int)Math.Round(o.Percent * 100);
                column.PerturbMode = o.Mode;
                column.IsCount = o.IsCount;
                break;
            case DateShiftOptions o:
                // Formats are not stored: they come from each file's profile.
                column.MaxDays = o.MaxDays;
                column.KeepWeekday = o.KeepWeekday;
                column.DateShiftMode = o.Mode;
                break;
            case RedactOptions o:
                column.RedactText = o.Text;
                break;
        }
        return column;
    }

    /// <param name="profile">The current file's column, for DateShift formats; null leaves them unset.</param>
    public static ColumnRule ToRule(RecipeColumn column, ColumnProfile? profile = null)
    {
        StrategyOptions? options = column.Strategy switch
        {
            MaskingStrategy.HashId => new HashIdOptions(column.Prefix ?? ""),
            MaskingStrategy.Fake => new FakeOptions(column.FakeKind ?? FakeKind.PersonFull),
            MaskingStrategy.Perturb => new PerturbOptions(
                (column.PerturbPercent ?? 15) / 100.0, column.PerturbMode ?? PerturbMode.PerRow, column.IsCount ?? false),
            MaskingStrategy.DateShift => new DateShiftOptions(
                column.MaxDays ?? 30, column.DateShiftMode ?? DateShiftMode.Global, column.KeepWeekday ?? false, profile?.DateFormats),
            MaskingStrategy.Redact => new RedactOptions(column.RedactText ?? RedactOptions.DefaultText),
            _ => null,
        };
        // A shared mapping is stored as a column name; RecipeMatcher.Apply resolves it to a key.
        return new ColumnRule(column.Strategy, options);
    }
}
