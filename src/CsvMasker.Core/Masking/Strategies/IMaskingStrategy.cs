using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>A non-empty source value plus its position. Row gives access to other columns (for entity groups later).</summary>
internal readonly record struct MaskInput(string Value, CsvRecord Row);

/// <summary>
/// One column's strategy. Strategies only transform non-blank values: nulls, empties and
/// whitespace never reach them (see <see cref="ColumnMasker"/>), nor does uniqueness enforcement.
/// </summary>
internal interface IMaskingStrategy
{
    /// <summary>
    /// Mapping strategies guarantee distinct inputs → distinct outputs (cardinality preserved),
    /// enforced by <see cref="ColumnMasker"/> through the domain's mapping store.
    /// </summary>
    bool IsMapping { get; }

    /// <summary>
    /// Masks a value. <paramref name="attempt"/> is 0 for the first derivation and counts up on
    /// collision retries (mapping strategies). Returns null when the value can't be masked by
    /// this strategy (e.g. "N/A" in a Perturb column); the caller redacts and counts it.
    /// </summary>
    string? Mask(in MaskInput input, int attempt);

    /// <summary>
    /// Last resort when retries keep colliding (a finite fake-data pool): a variant of
    /// <paramref name="candidate"/> made distinct by <paramref name="n"/>. Null when the strategy
    /// can't disambiguate without breaking the format.
    /// </summary>
    string? Disambiguate(string candidate, int n) => null;

    /// <summary>A column-level note for the verification report (e.g. ZIPs derived without a reference list).</summary>
    string? Warning => null;
}
