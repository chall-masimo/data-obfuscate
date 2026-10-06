namespace CsvMasker.Core.Csv;

/// <summary>
/// The header row. <see cref="OriginalNames"/> are written back untouched; <see cref="Keys"/> are
/// unique names for internal use when the source has duplicate or blank column names.
/// </summary>
public sealed class CsvHeader
{
    private readonly Dictionary<string, int> _indexByKey;

    public CsvHeader(CsvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Keys = BuildKeys(record.Values);
        _indexByKey = new Dictionary<string, int>(Keys.Count, StringComparer.Ordinal);
        for (int i = 0; i < Keys.Count; i++)
            _indexByKey.Add(Keys[i], i);
    }

    /// <summary>The header exactly as read, for writing back.</summary>
    public CsvRecord Record { get; }

    public IReadOnlyList<string> OriginalNames => Record.Values;

    public IReadOnlyList<string> Keys { get; }

    public int Count => Keys.Count;

    public int IndexOf(string key) => _indexByKey.TryGetValue(key, out int index) ? index : -1;

    /// <summary>
    /// Makes unique keys. The first occurrence of a name keeps it; later duplicates become
    /// <c>Name#2</c>, <c>Name#3</c>…; blank or whitespace-only names become <c>Column{n}</c>
    /// (1-based position). A generated key never takes a name that appears in the header, so a
    /// real column called <c>Name#2</c> keeps its name. Comparison is ordinal (case-sensitive).
    /// </summary>
    public static IReadOnlyList<string> BuildKeys(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var realNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(name))
                realNames.Add(name);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var keys = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            bool blank = string.IsNullOrWhiteSpace(name);
            if (!blank && used.Add(name))
            {
                keys[i] = name;
                continue;
            }

            string stem = blank ? $"Column{i + 1}" : name;
            int suffix = blank ? 1 : 2;
            string key = suffix == 1 ? stem : $"{stem}#{suffix}";
            while (used.Contains(key) || realNames.Contains(key))
                key = $"{stem}#{++suffix}";

            used.Add(key);
            keys[i] = key;
        }

        return keys;
    }
}
