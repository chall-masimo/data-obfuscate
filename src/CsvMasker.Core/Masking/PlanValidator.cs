using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Masking;

/// <summary>Checks a plan against the file's header before any data is read (fail closed).</summary>
internal static class PlanValidator
{
    public static void Validate(MaskingPlan plan, CsvHeader header)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(header);

        var problems = new List<string>();
        var headerKeys = new HashSet<string>(header.Keys, StringComparer.Ordinal);

        foreach (var key in header.Keys)
            if (!plan.Columns.ContainsKey(key))
                problems.Add($"Column '{key}' has no strategy assigned. Every column needs one, including Keep.");

        foreach (var key in plan.Columns.Keys)
            if (!headerKeys.Contains(key))
                problems.Add($"The plan has a rule for '{key}', which is not a column in this file.");

        foreach (var (key, rule) in plan.Columns)
        {
            if (rule.MappingDomain is { } domain && string.IsNullOrWhiteSpace(domain))
                problems.Add($"Column '{key}': the mapping domain cannot be blank.");
            if (OptionsProblem(rule) is { } problem)
                problems.Add($"Column '{key}': {problem}");
        }

        // Mapping strategies share one source→output map per domain, so every column in a
        // domain must produce values the same way.
        var byDomain = plan.Columns
            .Where(c => IsMapping(c.Value.Strategy))
            .GroupBy(c => c.Value.MappingDomain ?? c.Key, StringComparer.Ordinal);
        foreach (var group in byDomain)
        {
            var first = group.First().Value;
            if (group.Any(c => c.Value.Strategy != first.Strategy || !Equals(c.Value.Options, first.Options)))
                problems.Add($"Columns {string.Join(", ", group.Select(c => $"'{c.Key}'"))} share mapping domain '{group.Key}' but have different strategies or options.");
        }

        if (problems.Count > 0)
            throw new MaskingPlanException(problems);
    }

    public static bool IsMapping(MaskingStrategy strategy) =>
        strategy is MaskingStrategy.HashId or MaskingStrategy.Fake or MaskingStrategy.ZipRemap;

    private static string? OptionsProblem(ColumnRule rule) => (rule.Strategy, rule.Options) switch
    {
        (MaskingStrategy.Keep or MaskingStrategy.ZipRemap, null) => null,
        (MaskingStrategy.HashId, null or HashIdOptions) => null,
        (MaskingStrategy.Fake, FakeOptions o) when !Enum.IsDefined(o.Kind) => "unknown fake kind.",
        (MaskingStrategy.Fake, FakeOptions) => null,
        (MaskingStrategy.Fake, null) => "Fake needs a kind (FakeOptions).",
        (MaskingStrategy.Perturb, PerturbOptions o) when o.Percent is not (> 0 and < 1) => "Perturb percent must be between 0 and 1.",
        (MaskingStrategy.Perturb, PerturbOptions { Mode: PerturbMode.PerEntity }) => "PerEntity perturbation needs entity groups, which are not available yet.",
        (MaskingStrategy.Perturb, null or PerturbOptions) => null,
        (MaskingStrategy.DateShift, DateShiftOptions o) when o.MaxDays < 1 => "DateShift max days must be at least 1.",
        (MaskingStrategy.DateShift, DateShiftOptions { KeepWeekday: true } o) when o.MaxDays < 7 => "DateShift with keep-weekday needs max days of at least 7.",
        (MaskingStrategy.DateShift, DateShiftOptions { Mode: DateShiftMode.PerEntity }) => "PerEntity date shifting needs entity groups, which are not available yet.",
        (MaskingStrategy.DateShift, null or DateShiftOptions) => null,
        (MaskingStrategy.Redact, null or RedactOptions) => null,
        (MaskingStrategy.Lorem, null or LoremOptions) => null,
        _ when !Enum.IsDefined(rule.Strategy) => "unknown strategy.",
        _ => $"options of type {rule.Options!.GetType().Name} don't apply to {rule.Strategy}.",
    };
}
