using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Profiling;

public sealed class FileProfile
{
    public required CsvDialect Dialect { get; init; }

    public required CsvHeader Header { get; init; }

    /// <summary>Data rows profiled (excluding blank lines and malformed rows).</summary>
    public required long RowsProfiled { get; init; }

    public required long BlankLines { get; init; }

    /// <summary>False when the file has more rows than the sample, so counts are estimates for the file.</summary>
    public required bool ReachedEndOfFile { get; init; }

    public required long MalformedRowCount { get; init; }

    /// <summary>Record numbers (header = 1) of the first malformed rows; see <see cref="MalformedRowCount"/> for the total.</summary>
    public required IReadOnlyList<long> MalformedRecordNumbers { get; init; }

    public required IReadOnlyList<ColumnProfile> Columns { get; init; }

    public override string ToString() =>
        $"{Columns.Count} columns, {RowsProfiled:N0} rows profiled{(ReachedEndOfFile ? "" : " (sample)")}, " +
        $"{MalformedRowCount:N0} malformed, {Dialect.Encoding.Name}";
}
