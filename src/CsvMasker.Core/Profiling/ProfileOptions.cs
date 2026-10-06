using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Profiling;

public sealed record ProfileOptions
{
    public static ProfileOptions Default { get; } = new();

    /// <summary>Data rows to read (<c>Limits:ProfileSampleRows</c>).</summary>
    public int SampleRows { get; init; } = 50_000;

    /// <summary>
    /// Distinct values counted exactly per column; beyond this the count is a HyperLogLog
    /// estimate. Bounds profiling memory on wide, high-cardinality files.
    /// </summary>
    public int ExactDistinctLimit { get; init; } = 1_000;

    public int SampleValueCount { get; init; } = 5;

    /// <summary>Malformed record numbers listed individually; the total is always reported.</summary>
    public int MaxMalformedRowsListed { get; init; } = 100;

    public CsvReaderOptions Reader { get; init; } = CsvReaderOptions.Default;

    public DetectionThresholds Thresholds { get; init; } = DetectionThresholds.Default;
}
