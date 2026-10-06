namespace CsvMasker.Core.Profiling;

/// <summary>Column types from the CLAUDE.md detection table, plus <see cref="Text"/> and <see cref="Empty"/>.</summary>
public enum DetectedType
{
    Identifier,
    Zip,
    Email,
    Phone,
    PersonName,
    OrgName,
    Address,
    City,
    State,
    Date,
    DateTime,
    Measure,
    Count,
    Boolean,
    Categorical,
    FreeText,

    /// <summary>Matched no rule. Redact is suggested, since the column may be sensitive.</summary>
    Text,

    /// <summary>No non-blank values in the sample.</summary>
    Empty,
}
