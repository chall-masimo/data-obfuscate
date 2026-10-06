namespace CsvMasker.Core.Profiling;

/// <summary>
/// Profile of one column. Counts are over the profiled sample. <see cref="SampleValues"/> holds
/// real data: show it on the review screen, never persist or log it. <see cref="ToString"/>
/// deliberately leaves it out.
/// </summary>
public sealed class ColumnProfile
{
    public required int Index { get; init; }

    /// <summary>Unique internal key (see <see cref="Csv.CsvHeader.Keys"/>).</summary>
    public required string Key { get; init; }

    public required string OriginalName { get; init; }

    public required DetectedType Type { get; init; }

    /// <summary>Why this type was chosen, for the review screen. Contains no cell values.</summary>
    public required string Reason { get; init; }

    /// <summary>Things the user should check (stripped ZIP zeros, ambiguous dates…). Contains no cell values.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public required StrategySuggestion Suggestion { get; init; }

    public required long RowCount { get; init; }

    /// <summary>Unquoted empty fields.</summary>
    public required long NullCount { get; init; }

    /// <summary>Quoted empty or whitespace-only fields.</summary>
    public required long BlankCount { get; init; }

    /// <summary>Non-null, non-blank values.</summary>
    public required long ValueCount { get; init; }

    public double NullRate => RowCount == 0 ? 0 : (double)NullCount / RowCount;

    public double BlankRate => RowCount == 0 ? 0 : (double)BlankCount / RowCount;

    /// <summary>Distinct non-blank values.</summary>
    public required long DistinctCount { get; init; }

    /// <summary>True when the count is a HyperLogLog estimate or the file is longer than the sample.</summary>
    public required bool DistinctIsEstimate { get; init; }

    public required int MinLength { get; init; }
    public required int MaxLength { get; init; }
    public required double AverageLength { get; init; }

    public required bool HasLeadingZeros { get; init; }

    public required bool IsNumeric { get; init; }
    public required bool IsIntegerOnly { get; init; }
    public required int MaxDecimalScale { get; init; }
    public required bool UsesThousandsSeparators { get; init; }
    public required bool UsesCurrencySymbol { get; init; }
    public required bool UsesParenthesesNegatives { get; init; }

    /// <summary>.NET format strings that together cover the column's values (date columns only).</summary>
    public required IReadOnlyList<string> DateFormats { get; init; }

    public required bool HasTimeComponent { get; init; }

    /// <summary>Sensitive. First distinct non-blank values; in memory only.</summary>
    public required IReadOnlyList<string> SampleValues { get; init; }

    public override string ToString() => $"{Key}: {Type} → {Suggestion}";
}
