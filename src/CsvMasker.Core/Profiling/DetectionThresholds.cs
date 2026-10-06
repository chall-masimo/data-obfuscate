namespace CsvMasker.Core.Profiling;

/// <summary>Tunable cut-offs for type detection. Ratios are distinct values ÷ non-blank values.</summary>
public sealed record DetectionThresholds
{
    public static DetectionThresholds Default { get; } = new();

    /// <summary>Share of non-blank values that must match a pattern for it to count.</summary>
    public double PatternMatchRatio { get; init; } = 0.95;

    /// <summary>Above this ratio a column counts as high-cardinality (identifier-like).</summary>
    public double IdentifierRatio { get; init; } = 0.5;

    /// <summary>Unhinted integer columns above this ratio are treated as identifiers, not measures.</summary>
    public double NearUniqueRatio { get; init; } = 0.9;

    public int CategoricalMaxDistinct { get; init; } = 50;

    public double CategoricalMaxRatio { get; init; } = 0.01;

    public double FreeTextMinAverageLength { get; init; } = 60;
}
