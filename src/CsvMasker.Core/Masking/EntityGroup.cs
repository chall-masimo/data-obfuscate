namespace CsvMasker.Core.Masking;

/// <summary>
/// Columns describing one real-world entity. Members are seeded from the <see cref="Anchor"/>
/// column's value, so one source entity (e.g. one Customer_ID) gets one coherent fake identity
/// across all its columns. Keys are <see cref="Csv.CsvHeader.Keys"/>.
/// </summary>
public sealed record EntityGroup(string Anchor, IReadOnlyList<string> Members)
{
    /// <summary>The seed domain shared by every member: same anchor value, same randomness.</summary>
    public string SeedDomain => "entity:" + Anchor;
}
