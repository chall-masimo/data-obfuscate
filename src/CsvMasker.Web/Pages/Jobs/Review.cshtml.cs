using System.ComponentModel.DataAnnotations;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Pages.Jobs;

/// <summary>
/// One row per column: what was detected and the strategy to use. Pre-filled from the profiler's
/// suggestions, but nothing runs until the user ticks "I have reviewed every column" (fail closed).
/// </summary>
public sealed class ReviewModel(JobRegistry registry, IOptions<LimitsOptions> limits, ILogger<ReviewModel> logger) : JobPageModel(registry)
{
    [BindProperty]
    public List<ColumnInput> Columns { get; set; } = [];

    [BindProperty]
    public bool Confirmed { get; set; }

    [BindProperty]
    public bool SkipMalformed { get; set; }

    public IReadOnlyList<string> PlanProblems { get; private set; } = [];

    public IActionResult OnGet(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
            return RedirectToStep();

        Columns = Job.Profile.Columns.Select(c => ColumnInput.From(c.Key, Job.Plan.Columns[c.Key])).ToList();
        SkipMalformed = Job.SkipMalformed;
        return Page();
    }

    public IActionResult OnPost(Guid id)
    {
        if (!TryLoad(id))
            return NotFound();
        if (Job.State is not (JobState.Uploaded or JobState.Reviewed))
            return RedirectToStep();

        if (!Confirmed)
            ModelState.AddModelError(nameof(Confirmed), "Tick the box to confirm you have reviewed every column.");

        var profiles = Job.Profile.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var rules = new Dictionary<string, ColumnRule>(StringComparer.Ordinal);
        foreach (var input in Columns)
            if (profiles.TryGetValue(input.Key, out var profile))
                rules.TryAdd(input.Key, input.ToRule(profile));

        var plan = new MaskingPlan(rules);
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

    public ColumnProfile ProfileFor(ColumnInput input) =>
        Job.Profile.Columns.FirstOrDefault(c => c.Key == input.Key) ?? Job.Profile.Columns[0];
}

/// <summary>The review form's fields for one column; every strategy's options are posted, only the chosen one is used.</summary>
public sealed class ColumnInput
{
    public string Key { get; set; } = "";

    public MaskingStrategy Strategy { get; set; }

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

    public static ColumnInput From(string key, ColumnRule rule)
    {
        var input = new ColumnInput { Key = key, Strategy = rule.Strategy };
        switch (rule.Options)
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
                break;
            case RedactOptions o: input.RedactText = o.Text; break;
        }
        return input;
    }

    public ColumnRule ToRule(ColumnProfile profile) => Strategy switch
    {
        MaskingStrategy.HashId => new(Strategy, new HashIdOptions(Prefix?.Trim() ?? "")),
        MaskingStrategy.Fake => new(Strategy, new FakeOptions(FakeKind)),
        MaskingStrategy.Perturb => new(Strategy, new PerturbOptions(PerturbPercent / 100.0, PerturbMode, IsCount)),
        MaskingStrategy.DateShift => new(Strategy, new DateShiftOptions(MaxDays, DateShiftMode.Global, KeepWeekday, profile.DateFormats)),
        MaskingStrategy.Redact => new(Strategy, new RedactOptions(string.IsNullOrEmpty(RedactText) ? RedactOptions.DefaultText : RedactText)),
        _ => new(Strategy),
    };
}
