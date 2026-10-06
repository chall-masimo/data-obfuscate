namespace CsvMasker.Core.Masking.Verification;

/// <summary>
/// Checks computed during the run. Contains counts and sums only, never values. Failures are
/// shown prominently; the download is still allowed but flagged (CLAUDE.md).
/// </summary>
public sealed class VerificationReport
{
    public required long RowsRead { get; init; }
    public required long RowsWritten { get; init; }
    public required long BlankLines { get; init; }

    /// <summary>Malformed rows skipped (skip mode only; they are left out of the output).</summary>
    public required long MalformedRowCount { get; init; }

    public required IReadOnlyList<long> MalformedRecordNumbers { get; init; }

    public required IReadOnlyList<ColumnVerification> Columns { get; init; }

    public bool RowCountMatches => RowsRead == RowsWritten;

    public bool HasFailures => !RowCountMatches || Columns.Any(c => c.Failures.Count > 0);

    public override string ToString() =>
        $"{RowsWritten:N0}/{RowsRead:N0} rows, {MalformedRowCount:N0} malformed skipped, " +
        $"{Columns.Count(c => c.Failures.Count > 0)} column(s) failed verification";
}

public sealed class ColumnVerification
{
    public required string Key { get; init; }
    public required MaskingStrategy Strategy { get; init; }

    /// <summary>Mapping strategies must keep distinct counts and never output a source value.</summary>
    public required bool IsMapping { get; init; }

    /// <summary>Linked to an entity group: distinct counts are of (anchor, value) pairs, so one entity's value counts once.</summary>
    public bool PerEntity { get; init; }

    public required long RowCount { get; init; }
    public required long SourceNullCount { get; init; }
    public required long OutputNullCount { get; init; }

    /// <summary>Distinct non-blank values. Exact for mapping columns, otherwise an estimate above 1,000.</summary>
    public required long SourceDistinct { get; init; }
    public required long OutputDistinct { get; init; }
    public required bool DistinctIsExact { get; init; }

    /// <summary>Non-blank values whose output equals the source.</summary>
    public required long UnchangedCount { get; init; }

    /// <summary>Fake values that needed a numeric suffix to stay unique (data pool exhausted).</summary>
    public required long DisambiguatedCount { get; init; }

    /// <summary>Values the strategy couldn't handle (e.g. "N/A" in a Perturb column), replaced with [REDACTED].</summary>
    public required long RedactedUnmaskableCount { get; init; }

    public required decimal? SourceSum { get; init; }
    public required decimal? OutputSum { get; init; }

    public decimal? SumDifferencePercent =>
        SourceSum is { } s && OutputSum is { } o && s != 0 ? Math.Round((o - s) / Math.Abs(s) * 100, 2) : null;

    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }

    public override string ToString() => $"{Key}: {Strategy}, {Failures.Count} failure(s)";
}
