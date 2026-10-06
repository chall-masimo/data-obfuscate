using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Masking;

/// <param name="MappingDomain">
/// Columns that share a domain map the same source value to the same output (e.g. BillTo and
/// ShipTo customer). Defaults to the column key.
/// </param>
public sealed record ColumnRule(MaskingStrategy Strategy, StrategyOptions? Options = null, string? MappingDomain = null);

/// <summary>
/// A strategy for every column, keyed by <see cref="Csv.CsvHeader.Keys"/>. Fail closed: a plan
/// that leaves any column unassigned is rejected before any data is read.
/// </summary>
public sealed class MaskingPlan
{
    public MaskingPlan(IReadOnlyDictionary<string, ColumnRule> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Columns = new Dictionary<string, ColumnRule>(columns, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, ColumnRule> Columns { get; }

    /// <summary>Checks the plan against a file's header (fail closed).</summary>
    /// <exception cref="MaskingPlanException">Any column is unassigned or misconfigured.</exception>
    public void Validate(Csv.CsvHeader header) => PlanValidator.Validate(this, header);

    /// <summary>
    /// A plan pre-filled from the profiler's suggestions. The user still confirms every column
    /// on the review screen; this is the pre-fill, not an approval.
    /// </summary>
    public static MaskingPlan FromSuggestions(FileProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var rules = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
        foreach (var column in profile.Columns)
        {
            var suggestion = column.Suggestion;
            StrategyOptions? options = suggestion.Strategy switch
            {
                MaskingStrategy.Fake => new FakeOptions(suggestion.FakeKind ?? FakeKind.PersonFull),
                MaskingStrategy.Perturb => new PerturbOptions(IsCount: column.Type == DetectedType.Count),
                MaskingStrategy.DateShift => new DateShiftOptions(Formats: column.DateFormats),
                _ => null,
            };
            rules[column.Key] = new ColumnRule(suggestion.Strategy, options);
        }
        return new MaskingPlan(rules);
    }
}
