using System.ComponentModel.DataAnnotations;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using CsvMasker.Web.Recipes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>
/// One row per column: what was detected and the strategy to use. Pre-filled from a matching
/// recipe or the profiler's suggestions. Columns a partial recipe didn't cover start blank.
/// Nothing runs until every column has a strategy and the user ticks "I have reviewed every
/// column" (fail closed).
/// </summary>
public sealed class ReviewModel(
    JobRegistry registry,
    RecipeStore recipes,
    IOptions<LimitsOptions> limits,
    ILogger<ReviewModel> logger) : JobPageModel(registry)
{
    [BindProperty]
    public List<ColumnInput> Columns { get; set; } = [];

    [BindProperty]
    public bool Confirmed { get; set; }

    [BindProperty]
    public bool SkipMalformed { get; set; }

    public IReadOnlyList<string> PlanProblems { get; private set; } = [];

    /// <summary>Every recipe scored against this file, best first, for the "pre-fill from" selector.</summary>
    public IReadOnlyList<RecipeMatch> RecipeChoices { get; private set; } = [];

    /// <summary>The profiler's suggestion per column key, shown as a hint beside unassigned columns.</summary>
    public IReadOnlyDictionary<string, ColumnRule> Suggestions { get; private set; } = new Dictionary<string, ColumnRule>();

    [TempData]
    public string? Notice { get; set; }

    public IActionResult OnGet(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
            return RedirectToStep();

        LoadChoices();
        Columns = Job.Profile.Columns
            .Select(c => ColumnInput.From(c.Key, Job.Plan.Columns.GetValueOrDefault(c.Key), Suggestions[c.Key], Job.Plan.AnchorOf(c.Key)))
            .ToList();
        SkipMalformed = Job.SkipMalformed;
        return Page();
    }

    public IActionResult OnPost(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
            return RedirectToStep();

        LoadChoices();
        if (!Confirmed)
            ModelState.AddModelError(nameof(Confirmed), "Tick the box to confirm you have reviewed every column.");

        ResolveSharedMappings();
        var profiles = Job.Profile.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var rules = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
        foreach (var input in Columns)
            if (profiles.TryGetValue(input.Key, out var profile) && input.ToRule(profile) is { } rule)
                rules.TryAdd(input.Key, rule);

        var plan = new MaskingPlan(rules, Groups());
        try
        {
            plan.Validate(Job.Profile.Header);
        }
        catch (MaskingPlanException ex)
        {
            PlanProblems = ex.Problems;
            ModelState.AddModelError("", "Some columns need attention.");
        }

        if (!ModelState.IsValid)
            return Page();

        lock (Job.Sync)
        {
            if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
                return RedirectToStep();
            Job.Plan = plan;
            Job.SkipMalformed = SkipMalformed;
            Job.Session?.Dispose();
            Job.Session = new MaskingSession(new MaskingOptions
            {
                MaxMappingEntries = limits.Value.MaxMappingEntries,
                FailOnMalformed = !SkipMalformed,
            });
            Job.State = JobState.Reviewed;
        }

        logger.LogInformation("Job {JobId} reviewed by {User}: {Strategies}", Job.Id, Job.Owner,
            string.Join(", ", plan.Columns.Values.GroupBy(r => r.Strategy).Select(g => $"{g.Key} x{g.Count()}")));
        return RedirectToPage("/Jobs/Preview", new { id });
    }

    /// <summary>Re-fills the form from another recipe, or from the profiler's suggestions when none is chosen.</summary>
    public IActionResult OnPostApplyRecipe(Guid id, Guid? recipeId)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
            return RedirectToStep();

        RecipeMatch? match = null;
        if (recipeId is { } chosen)
        {
            var recipe = recipes.Get(chosen);
            if (recipe is null)
            {
                Notice = "That recipe no longer exists.";
                return RedirectToPage(new { id });
            }
            match = RecipeMatcher.Rank(Job.Profile.Header.OriginalNames, [recipe])[0];
        }

        lock (Job.Sync)
        {
            if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
                return RedirectToStep();
            int droppedLinks = 0;
            Job.Plan = match is null ? MaskingPlan.FromSuggestions(Job.Profile) : RecipeMatcher.Apply(Job.Profile, match.Recipe, out droppedLinks);
            Job.Recipe = match is null ? null : AppliedRecipe.From(match, droppedLinks);
            Job.SkipMalformed = match?.Recipe.SkipMalformed ?? false;
            // A different pre-fill needs a fresh review and confirmation.
            Job.Session?.Dispose();
            Job.Session = null;
            Job.State = JobState.Uploaded;
        }

        logger.LogInformation("Job {JobId}: {User} applied recipe {RecipeId}", Job.Id, Job.Owner, match?.Recipe.Id);
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// "Same mapping as" chains (A → B → C) all share the root column's domain, so A, B and C map
    /// identically. A cycle is broken by leaving the column on its own mapping.
    /// </summary>
    private void ResolveSharedMappings()
    {
        var partner = Columns.Where(c => !string.IsNullOrEmpty(c.SameMappingAs)).ToDictionary(c => c.Key, c => c.SameMappingAs!, StringComparer.Ordinal);
        foreach (var input in Columns.Where(c => !string.IsNullOrEmpty(c.SameMappingAs)))
        {
            string root = input.SameMappingAs!;
            var visited = new HashSet<string>(StringComparer.Ordinal) { input.Key };
            while (partner.TryGetValue(root, out var next) && visited.Add(root))
                root = next;
            input.SameMappingAs = root == input.Key ? null : root;
        }
    }

    /// <summary>Entity groups as currently chosen on the form: columns grouped by the anchor they're linked to.</summary>
    public IReadOnlyList<EntityGroup> Groups() =>
        Columns.Where(c => !string.IsNullOrEmpty(c.LinkedTo))
            .GroupBy(c => c.LinkedTo!, StringComparer.Ordinal)
            .Select(g => new EntityGroup(g.Key, g.Select(c => c.Key).ToArray()))
            .ToArray();

    public string NameOf(string key) =>
        Job.Profile.Columns.FirstOrDefault(c => c.Key == key) is { } c && !string.IsNullOrWhiteSpace(c.OriginalName) ? c.OriginalName : key;

    /// <summary>Anchor candidates: identifier columns (plus the current choice), excluding the column itself.</summary>
    public IEnumerable<SelectListItem> AnchorChoices(string key, string? current) =>
        Job.Profile.Columns
            .Where(c => c.Key != key && (c.Type == DetectedType.Identifier || c.Key == current))
            .Select(c => new SelectListItem($"Linked to {NameOf(c.Key)}", c.Key));

    /// <summary>Columns to share a mapping with: same detected type (plus the current choice).</summary>
    public IEnumerable<SelectListItem> DomainChoices(string key, string? current)
    {
        var type = Job.Profile.Columns.First(c => c.Key == key).Type;
        return Job.Profile.Columns
            .Where(c => c.Key != key && (c.Type == type || c.Key == current))
            .Select(c => new SelectListItem($"Same mapping as {NameOf(c.Key)}", c.Key));
    }

    /// <summary>Things to know about a linked column (distribution change, shared fake person).</summary>
    public IEnumerable<string> NotesFor(int index)
    {
        var input = Columns[index];
        if (string.IsNullOrEmpty(input.LinkedTo))
            yield break;

        var column = ProfileFor(input);
        var anchor = Job.Profile.Columns.FirstOrDefault(c => c.Key == input.LinkedTo);
        if (anchor is not null && column.DistinctCount < anchor.DistinctCount * 0.5)
            yield return $"{NameOf(column.Key)} has {(column.DistinctIsEstimate ? "about " : "")}{column.DistinctCount:N0} distinct values across "
                + $"{(anchor.DistinctIsEstimate ? "about " : "")}{anchor.DistinctCount:N0} {NameOf(anchor.Key)} values: linking gives each one its own value "
                + $"and changes {NameOf(column.Key)}'s distribution.";

        if (input.Strategy == MaskingStrategy.Fake && input.FakeKind is FakeKind.PersonFull)
        {
            var others = Columns.Where(c => c != input && c.LinkedTo == input.LinkedTo && c.Strategy == MaskingStrategy.Fake && c.FakeKind == FakeKind.PersonFull)
                .Select(c => NameOf(c.Key)).ToList();
            if (others.Count > 0)
                yield return $"{NameOf(column.Key)} and {string.Join(", ", others)} are both person names linked to {NameOf(input.LinkedTo)}: they'll get the same fake person.";
        }
    }

    public ColumnProfile ProfileFor(ColumnInput input) =>
        Job.Profile.Columns.FirstOrDefault(c => c.Key == input.Key) ?? Job.Profile.Columns[0];

    private void LoadChoices()
    {
        RecipeChoices = RecipeMatcher.Rank(Job.Profile.Header.OriginalNames, recipes.List());
        Suggestions = MaskingPlan.FromSuggestions(Job.Profile).Columns;
    }
}

/// <summary>The review form's fields for one column; every strategy's options are posted, only the chosen one is used.</summary>
public sealed class ColumnInput
{
    public string Key { get; set; } = "";

    /// <summary>Null until chosen: a column a partial recipe didn't cover.</summary>
    public MaskingStrategy? Strategy { get; set; }

    public FakeKind FakeKind { get; set; } = FakeKind.PersonFull;

    [StringLength(20)]
    public string? Prefix { get; set; }

    [Range(1, 99, ErrorMessage = "Perturb percent must be between 1 and 99.")]
    public int PerturbPercent { get; set; } = 15;

    public PerturbMode PerturbMode { get; set; } = PerturbMode.PerRow;

    public bool IsCount { get; set; }

    [Range(1, 3650, ErrorMessage = "Max days must be between 1 and 3650.")]
    public int MaxDays { get; set; } = 30;

    public bool KeepWeekday { get; set; }

    [StringLength(100)]
    public string? RedactText { get; set; }

    public DateShiftMode DateShiftMode { get; set; } = DateShiftMode.Global;

    /// <summary>Key of the anchor column this column is linked to (entity group), or empty.</summary>
    public string? LinkedTo { get; set; }

    /// <summary>Key of a column whose mapping this column shares (mapping domain), or empty.</summary>
    public string? SameMappingAs { get; set; }

    /// <summary>
    /// Fields for a column. With no rule the strategy is left blank, but the options are
    /// pre-set from the suggestion so picking the suggested strategy gives sensible defaults.
    /// </summary>
    public static ColumnInput From(string key, ColumnRule? rule, ColumnRule suggestion, string? anchor)
    {
        var source = rule ?? suggestion;
        var input = new ColumnInput
        {
            Key = key,
            Strategy = rule?.Strategy,
            LinkedTo = anchor,
            SameMappingAs = rule?.MappingDomain is { } domain && domain != key ? domain : null,
        };
        switch (source.Options)
        {
            case HashIdOptions o: input.Prefix = o.Prefix; break;
            case FakeOptions o: input.FakeKind = o.Kind; break;
            case PerturbOptions o:
                input.PerturbPercent = (int)Math.Round(o.Percent * 100);
                input.PerturbMode = o.Mode;
                input.IsCount = o.IsCount;
                break;
            case DateShiftOptions o:
                input.MaxDays = o.MaxDays;
                input.KeepWeekday = o.KeepWeekday;
                input.DateShiftMode = o.Mode;
                break;
            case RedactOptions o: input.RedactText = o.Text; break;
        }
        return input;
    }

    /// <summary>The rule for this column, or null while no strategy is chosen.</summary>
    public ColumnRule? ToRule(ColumnProfile profile)
    {
        string? domain = string.IsNullOrEmpty(SameMappingAs) ? null : SameMappingAs;
        return Strategy switch
        {
            null => null,
            MaskingStrategy.HashId => new(MaskingStrategy.HashId, new HashIdOptions(Prefix?.Trim() ?? ""), domain),
            MaskingStrategy.Fake => new(MaskingStrategy.Fake, new FakeOptions(FakeKind), domain),
            MaskingStrategy.Perturb => new(MaskingStrategy.Perturb, new PerturbOptions(PerturbPercent / 100.0, PerturbMode, IsCount), domain),
            MaskingStrategy.DateShift => new(MaskingStrategy.DateShift, new DateShiftOptions(MaxDays, DateShiftMode, KeepWeekday, profile.DateFormats), domain),
            MaskingStrategy.Redact => new(MaskingStrategy.Redact, new RedactOptions(string.IsNullOrEmpty(RedactText) ? RedactOptions.DefaultText : RedactText), domain),
            { } strategy => new(strategy, null, domain),
        };
    }
}
