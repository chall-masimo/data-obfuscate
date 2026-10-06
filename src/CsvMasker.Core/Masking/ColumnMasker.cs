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
/// A column linked to an entity group is seeded from the row's anchor value instead of its own,
/// and its mapping is keyed by (anchor, value). One entity always gets the same output, and
/// within one entity distinct values get distinct outputs. Different entities may share an
/// output, as different real customers share a first name, which keeps every linked column of
/// an entity drawing the same seed (name and email agree). Rows with a blank anchor fall back
/// to ordinary value-seeded masking.
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
    private readonly MappingStore.Domain? _mappings;
    private readonly string _domain;
    private readonly ColumnVerifier _verifier;
    private readonly int? _anchorIndex;
    private readonly string? _entityDomain;

    /// <param name="domain">The column's mapping domain (its key unless shared).</param>
    /// <param name="anchorIndex">Index of the anchor column when this column is linked to an entity group.</param>
    /// <param name="entityDomain">The group's seed domain (<see cref="EntityGroup.SeedDomain"/>) when linked.</param>
    public ColumnMasker(
        string columnKey, IMaskingStrategy strategy, string domain, MappingStore store, ColumnVerifier verifier,
        int? anchorIndex = null, string? entityDomain = null)
    {
        _columnKey = columnKey;
        _strategy = strategy;
        _store = store;
        _domain = domain;
        _verifier = verifier;
        _anchorIndex = anchorIndex;
        _entityDomain = entityDomain;
        if (strategy.IsMapping)
            _mappings = store.Get(anchorIndex is null ? domain : $"{entityDomain}\u001E{columnKey}");
    }

    public string Mask(string value, bool isNull, CsvRecord row)
    {
        if (isNull || string.IsNullOrWhiteSpace(value))
            return value;

        string? anchor = AnchorValue(row);
        var input = anchor is null
            ? new MaskInput(value, row, _domain, value)
            : new MaskInput(value, row, _entityDomain!, anchor, LinkedToEntity: true);

        if (_mappings is null)
            return _strategy.Mask(input, 0) ?? Unmaskable();

        string key = MappingKey(value, anchor);
        if (_mappings.Map.TryGetValue(key, out var cached))
            return cached;

        string? output = DeriveUnique(input, anchor);
        if (output is null)
            return Unmaskable(); // not cached: a redaction isn't a unique mapping

        _store.Add(_mappings, key, output, MappingKey(output, anchor), _columnKey);
        return output;
    }

    /// <summary>The row's anchor value, or null when unlinked or the anchor is blank.</summary>
    public string? AnchorValue(CsvRecord row)
    {
        if (_anchorIndex is not { } index || row.IsNull(index))
            return null;
        string anchor = row.Values[index];
        return string.IsNullOrWhiteSpace(anchor) ? null : anchor;
    }

    /// <summary>Linked columns map (anchor, value) pairs; unlinked ones (and blank-anchor rows) map the value.</summary>
    internal static string MappingKey(string value, string? anchor) =>
        anchor is null ? value : anchor + "\u001F" + value;

    private string? DeriveUnique(in MaskInput input, string? anchor)
    {
        string? first = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt == RetriesBeforeDisambiguating && first is not null && _strategy.Disambiguate(first, 2) is not null)
                return Disambiguate(first, input.Value, anchor);

            string? candidate = _strategy.Mask(input, attempt);
            if (candidate is null)
                return null;
            first ??= candidate;
            if (IsAvailable(candidate, input.Value, anchor))
                return candidate;
        }
        throw MaskingException.UniqueValueExhausted(_columnKey);
    }

    private string Disambiguate(string candidate, string source, string? anchor)
    {
        for (int n = 2; n < int.MaxValue; n++)
        {
            string variant = _strategy.Disambiguate(candidate, n)!;
            if (IsAvailable(variant, source, anchor))
            {
                _verifier.Disambiguated++;
                return variant;
            }
        }
        throw MaskingException.UniqueValueExhausted(_columnKey);
    }

    /// <summary>Not the source itself, and not already taken: in the column, or (linked) within the same entity.</summary>
    private bool IsAvailable(string candidate, string source, string? anchor) =>
        !string.Equals(candidate, source, StringComparison.Ordinal) && !_mappings!.Used.Contains(MappingKey(candidate, anchor));

    private string Unmaskable()
    {
        _verifier.Unmaskable++;
        return RedactOptions.DefaultText;
    }
}
