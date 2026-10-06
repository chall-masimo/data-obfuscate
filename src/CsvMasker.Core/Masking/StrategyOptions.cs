namespace CsvMasker.Core.Masking;

/// <summary>Per-strategy options. Recipes store these, so they hold settings only, never data.</summary>
public abstract record StrategyOptions;

/// <param name="Prefix">Prepended to every masked value, e.g. "T-".</param>
public sealed record HashIdOptions(string Prefix = "") : StrategyOptions;

/// <param name="PreserveCase">Copy the source's ALL CAPS / all lower casing onto the fake value.</param>
public sealed record FakeOptions(FakeKind Kind, bool PreserveCase = true) : StrategyOptions;

public enum PerturbMode
{
    /// <summary>A different factor for every row.</summary>
    PerRow,

    /// <summary>One factor for the whole column (or mapping domain).</summary>
    Global,

    /// <summary>Factor seeded from an entity group's anchor value. Not available until entity groups (build step 7).</summary>
    PerEntity,
}

/// <param name="Percent">Maximum relative change, e.g. 0.15 for ±15%. Must be between 0 and 1.</param>
/// <param name="IsCount">Count column: whole numbers that never drop below 1 when the source is at least 1.</param>
public sealed record PerturbOptions(double Percent = 0.15, PerturbMode Mode = PerturbMode.PerRow, bool IsCount = false) : StrategyOptions;

public enum DateShiftMode
{
    /// <summary>One offset for the whole column (or mapping domain).</summary>
    Global,

    /// <summary>Offset seeded from an entity group's anchor value. Not available until entity groups (build step 7).</summary>
    PerEntity,
}

/// <param name="MaxDays">Shift is a whole number of days in [-MaxDays, -1] ∪ [1, MaxDays].</param>
/// <param name="KeepWeekday">Shift by whole weeks only.</param>
/// <param name="Formats">Profiled .NET date formats to try first; the full candidate list is tried after.</param>
public sealed record DateShiftOptions(
    int MaxDays = 30,
    DateShiftMode Mode = DateShiftMode.Global,
    bool KeepWeekday = false,
    IReadOnlyList<string>? Formats = null) : StrategyOptions;

public sealed record RedactOptions(string Text = RedactOptions.DefaultText) : StrategyOptions
{
    public const string DefaultText = "[REDACTED]";
}

public sealed record LoremOptions : StrategyOptions;
