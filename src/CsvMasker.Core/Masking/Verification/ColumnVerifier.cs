using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Masking.Verification;

/// <summary>
/// Per-column counters fed during the run. Mapping columns count distinct values exactly with
/// 64-bit hash sets, independently of the mapping dictionary, so a broken mapping is caught
/// rather than trusted. Other columns use bounded estimates.
/// </summary>
internal sealed class ColumnVerifier
{
    private const int ExactDistinctLimit = 1_000;

    private readonly string _key;
    private readonly MaskingStrategy _strategy;
    private readonly bool _isMapping;
    private readonly bool _sums;
    private readonly HashSet<ulong>? _sourceHashes, _outputHashes;
    private readonly DistinctCounter? _sourceEstimate, _outputEstimate;
    private long _rows, _sourceNulls, _outputNulls, _unchanged;
    private decimal _sourceSum, _outputSum;

    public ColumnVerifier(string key, MaskingStrategy strategy, bool isMapping)
    {
        _key = key;
        _strategy = strategy;
        _isMapping = isMapping;
        _sums = strategy == MaskingStrategy.Perturb;
        if (isMapping)
        {
            _sourceHashes = [];
            _outputHashes = [];
        }
        else
        {
            _sourceEstimate = new DistinctCounter(ExactDistinctLimit);
            _outputEstimate = new DistinctCounter(ExactDistinctLimit);
        }
    }

    public long Disambiguated { get; set; }

    public long Unmaskable { get; set; }

    public string? Warning { get; init; }

    /// <summary>Linked to an entity group: distinct counts are of (anchor, value) pairs.</summary>
    public bool PerEntity { get; init; }

    /// <param name="anchor">The row's anchor value for a linked column (null when unlinked or blank).</param>
    public void Record(string source, bool sourceIsNull, string output, bool outputIsNull, string? anchor = null)
    {
        _rows++;
        if (sourceIsNull) _sourceNulls++;
        if (outputIsNull) _outputNulls++;

        if (!sourceIsNull && !string.IsNullOrWhiteSpace(source))
        {
            if (_isMapping) _sourceHashes!.Add(DistinctCounter.Hash(ColumnMasker.MappingKey(source, anchor)));
            else _sourceEstimate!.Add(source);
            if (string.Equals(source, output, StringComparison.Ordinal)) _unchanged++;
        }
        if (!outputIsNull && !string.IsNullOrWhiteSpace(output))
        {
            if (_isMapping) _outputHashes!.Add(DistinctCounter.Hash(ColumnMasker.MappingKey(output, anchor)));
            else _outputEstimate!.Add(output);
        }

        if (_sums && NumberFormat.TryParse(source, out var s) && NumberFormat.TryParse(output, out var o))
        {
            _sourceSum += s.Value;
            _outputSum += o.Value;
        }
    }

    public ColumnVerification Build()
    {
        long sourceDistinct = _isMapping ? _sourceHashes!.Count : _sourceEstimate!.Count;
        long outputDistinct = _isMapping ? _outputHashes!.Count : _outputEstimate!.Count;
        var failures = new List<string>();
        var warnings = new List<string>();

        if (_sourceNulls != _outputNulls)
            failures.Add($"Null count changed ({_sourceNulls:N0} → {_outputNulls:N0}).");
        if (_isMapping && sourceDistinct != outputDistinct)
            failures.Add($"Distinct count changed ({sourceDistinct:N0} → {outputDistinct:N0}).");
        if (_isMapping && _unchanged > 0)
            failures.Add($"{_unchanged:N0} value(s) were not changed by masking.");

        if (Unmaskable > 0)
            warnings.Add($"{Unmaskable:N0} value(s) couldn't be masked by {_strategy} and were replaced with {RedactOptions.DefaultText}.");
        if (Disambiguated > 0)
            warnings.Add($"{Disambiguated:N0} fake value(s) got a numeric suffix to stay unique (the fake-data pool ran out).");
        if (Warning is not null)
            warnings.Add(Warning);

        return new ColumnVerification
        {
            Key = _key,
            Strategy = _strategy,
            IsMapping = _isMapping,
            PerEntity = PerEntity,
            RowCount = _rows,
            SourceNullCount = _sourceNulls,
            OutputNullCount = _outputNulls,
            SourceDistinct = sourceDistinct,
            OutputDistinct = outputDistinct,
            DistinctIsExact = _isMapping || (_sourceEstimate!.IsExact && _outputEstimate!.IsExact),
            UnchangedCount = _unchanged,
            DisambiguatedCount = Disambiguated,
            RedactedUnmaskableCount = Unmaskable,
            SourceSum = _sums ? _sourceSum : null,
            OutputSum = _sums ? _outputSum : null,
            Warnings = warnings,
            Failures = failures,
        };
    }
}
