using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Masking.Verification;

namespace CsvMasker.Core.Masking;

/// <summary>
/// Masks one column's values and enforces the universal invariants centrally, so no strategy can
/// break them:
/// <list type="bullet">
/// <item>null, empty and whitespace-only values pass through unchanged;</item>
/// <item>mapping strategies: a source value always gets the same output, distinct sources get
/// distinct outputs, and no output equals its source;</item>
/// <item>values a strategy can't handle are redacted and counted, never passed through.</item>
/// </list>
/// </summary>
internal sealed class ColumnMasker
{
    /// <summary>Collision retries before a strategy's disambiguator (e.g. a suffix) is used.</summary>
    internal const int RetriesBeforeDisambiguating = 50;

    /// <summary>Retries before giving up on a strategy that can't disambiguate.</summary>
    internal const int MaxRetries = 10_000;

    private readonly string _columnKey;
    private readonly IMaskingStrategy _strategy;
    private readonly MappingStore _store;
    private readonly MappingStore.Domain? _domain;
    private readonly ColumnVerifier _verifier;

    public ColumnMasker(string columnKey, IMaskingStrategy strategy, string domain, MappingStore store, ColumnVerifier verifier)
    {
        _columnKey = columnKey;
        _strategy = strategy;
        _store = store;
        _verifier = verifier;
        _domain = strategy.IsMapping ? store.Get(domain) : null;
    }

    public string Mask(string value, bool isNull, CsvRecord row)
    {
        if (isNull || string.IsNullOrWhiteSpace(value))
            return value;

        var input = new MaskInput(value, row);
        if (_domain is null)
            return _strategy.Mask(input, 0) ?? Unmaskable();

        if (_domain.Map.TryGetValue(value, out var cached))
            return cached;

        string? output = DeriveUnique(input);
        if (output is null)
            return Unmaskable(); // not cached: a redaction isn't a unique mapping

        _store.Add(_domain, value, output, _columnKey);
        return output;
    }

    private string? DeriveUnique(in MaskInput input)
    {
        string? first = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt == RetriesBeforeDisambiguating && first is not null && _strategy.Disambiguate(first, 2) is not null)
                return Disambiguate(first, input.Value);

            string? candidate = _strategy.Mask(input, attempt);
            if (candidate is null)
                return null;
            first ??= candidate;
            if (IsAvailable(candidate, input.Value))
                return candidate;
        }
        throw MaskingException.UniqueValueExhausted(_columnKey);
    }

    private string Disambiguate(string candidate, string source)
    {
        for (int n = 2; n < int.MaxValue; n++)
        {
            string variant = _strategy.Disambiguate(candidate, n)!;
            if (IsAvailable(variant, source))
            {
                _verifier.Disambiguated++;
                return variant;
            }
        }
        throw MaskingException.UniqueValueExhausted(_columnKey);
    }

    private bool IsAvailable(string candidate, string source) =>
        !string.Equals(candidate, source, StringComparison.Ordinal) && !_domain!.Used.Contains(candidate);

    private string Unmaskable()
    {
        _verifier.Unmaskable++;
        return RedactOptions.DefaultText;
    }
}
