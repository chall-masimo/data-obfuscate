namespace CsvMasker.Core.Masking;

/// <summary>The plan can't run. Messages name columns (header names) only, never cell values.</summary>
public sealed class MaskingPlanException(IReadOnlyList<string> problems)
    : Exception("The masking plan is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public enum MaskingErrorKind
{
    /// <summary>More source→output mappings than <c>Limits:MaxMappingEntries</c> allows.</summary>
    MappingLimitExceeded,

    /// <summary>No unused masked value could be found (e.g. HashId on single-digit IDs).</summary>
    UniqueValueExhausted,
}

/// <summary>A job failed cleanly mid-run. The message names the column and limit only, never cell values.</summary>
public sealed class MaskingException : Exception
{
    private MaskingException(MaskingErrorKind kind, string columnKey, string message) : base(message)
    {
        Kind = kind;
        ColumnKey = columnKey;
    }

    public MaskingErrorKind Kind { get; }

    public string ColumnKey { get; }

    internal static MaskingException MappingLimitExceeded(string columnKey, long limit) =>
        new(MaskingErrorKind.MappingLimitExceeded, columnKey,
            $"Column '{columnKey}': the file has more distinct values than the limit of {limit:N0} mapping entries. " +
            "Use a non-mapping strategy (Redact, Perturb, Keep) for high-cardinality columns, or raise Limits:MaxMappingEntries.");

    internal static MaskingException UniqueValueExhausted(string columnKey) =>
        new(MaskingErrorKind.UniqueValueExhausted, columnKey,
            $"Column '{columnKey}': could not find an unused masked value that keeps the source format. " +
            "The values are probably too short to mask uniquely; choose another strategy for this column.");
}
