namespace CsvMasker.Core.Csv;

public sealed record CsvReaderOptions
{
    public static CsvReaderOptions Default { get; } = new();

    /// <summary>
    /// Upper bound on the characters in one record. Stops a missing closing quote from turning the
    /// rest of a large file into one field held in memory.
    /// </summary>
    public int MaxRecordChars { get; init; } = 1_000_000;
}
