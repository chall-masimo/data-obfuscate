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

        GroupProblems(plan, headerKeys, problems);

        foreach (var (key, rule) in plan.Columns)
        {
            bool linked = plan.AnchorOf(key) is not null;
            if (rule.MappingDomain is { } domain && string.IsNullOrWhiteSpace(domain))
                problems.Add($"Column '{key}': the mapping domain cannot be blank.");
            if (linked && rule.MappingDomain is not null)
                problems.Add($"Column '{key}' is linked to an entity group, so it can't also share a mapping domain.");
            if (OptionsProblem(rule) is { } problem)
                problems.Add($"Column '{key}': {problem}");
            if (!linked && rule.Options is PerturbOptions { Mode: PerturbMode.PerEntity } or DateShiftOptions { Mode: DateShiftMode.PerEntity })
                problems.Add($"Column '{key}': Per entity needs the column to be linked to an entity group (an anchor column).");
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

    /// <summary>
    /// Each group has a real anchor and at least one member, no column belongs to two groups,
    /// and an anchor isn't itself linked (no chains).
    /// </summary>
    private static void GroupProblems(MaskingPlan plan, HashSet<string> headerKeys, List<string> problems)
    {
        var anchors = plan.EntityGroups.Select(g => g.Anchor).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in plan.EntityGroups)
        {
            if (!headerKeys.Contains(group.Anchor))
                problems.Add($"Entity group anchor '{group.Anchor}' is not a column in this file.");
            if (group.Members.Count == 0)
                problems.Add($"Entity group anchored on '{group.Anchor}' has no linked columns.");
            foreach (var member in group.Members)
            {
                if (!headerKeys.Contains(member))
                    problems.Add($"Column '{member}' is linked to '{group.Anchor}' but is not a column in this file.");
                else if (member == group.Anchor)
                    problems.Add($"Column '{member}' can't be linked to itself.");
                else if (anchors.Contains(member))
                    problems.Add($"Column '{member}' is an anchor, so it can't also be linked to '{group.Anchor}'.");
                else if (!seen.Add(member))
                    problems.Add($"Column '{member}' is linked to more than one anchor.");
            }
        }
        if (plan.EntityGroups.GroupBy(g => g.Anchor, StringComparer.Ordinal).Any(g => g.Count() > 1))
            problems.Add("Each anchor column can have only one entity group.");
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
        (MaskingStrategy.Perturb, null or PerturbOptions) => null,
        (MaskingStrategy.DateShift, DateShiftOptions o) when o.MaxDays < 1 => "DateShift max days must be at least 1.",
        (MaskingStrategy.DateShift, DateShiftOptions { KeepWeekday: true } o) when o.MaxDays < 7 => "DateShift with keep-weekday needs max days of at least 7.",
        (MaskingStrategy.DateShift, null or DateShiftOptions) => null,
        (MaskingStrategy.Redact, null or RedactOptions) => null,
        (MaskingStrategy.Lorem, null or LoremOptions) => null,
        _ when !Enum.IsDefined(rule.Strategy) => "unknown strategy.",
        _ => $"options of type {rule.Options!.GetType().Name} don't apply to {rule.Strategy}.",
    };
}
