namespace CsvMasker.Core.Masking;

/// <summary>
/// Source→output maps for mapping strategies, one per domain, plus the set of outputs already
/// used. These dictionaries are the job's main memory cost, so the total entry count is capped:
/// going over fails the job cleanly instead of letting IIS recycle the app pool mid-run.
/// </summary>
internal sealed class MappingStore(long maxEntries)
{
    private readonly Dictionary<string, Domain> _domains = new(StringComparer.Ordinal);

    public long EntryCount { get; private set; }

    public Domain Get(string name)
    {
        if (!_domains.TryGetValue(name, out var domain))
            _domains[name] = domain = new Domain();
        return domain;
    }

    /// <param name="usedKey">What counts as "taken": the output itself, or (anchor, output) for linked columns.</param>
    public void Add(Domain domain, string source, string output, string usedKey, string columnKey)
    {
        if (EntryCount >= maxEntries)
            throw MaskingException.MappingLimitExceeded(columnKey, maxEntries);
        domain.Map.Add(source, output);
        domain.Used.Add(usedKey);
        EntryCount++;
    }

    internal sealed class Domain
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Used { get; } = new(StringComparer.Ordinal);
    }
}
