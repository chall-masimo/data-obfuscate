using CsvMasker.Core.Masking;

namespace CsvMasker.Core.Profiling;

/// <summary>
/// Suggests entity groups by column name. An identifier such as <c>Customer_ID</c> anchors the
/// identity columns that share its prefix: <c>Customer_Name</c>, <c>Customer_Email</c>,
/// <c>Customer_Phone</c>, <c>Customer_Address</c>. Shared attributes (city, state, ZIP,
/// categories) are never suggested, because linking gives each entity its own value and
/// changes their distribution.
/// </summary>
internal static class EntityGroupSuggester
{
    private static readonly HashSet<DetectedType> IdentityTypes =
        [DetectedType.PersonName, DetectedType.OrgName, DetectedType.Email, DetectedType.Phone, DetectedType.Address];

    public static IReadOnlyList<EntityGroup> Suggest(FileProfile profile)
    {
        var tokens = profile.Columns.ToDictionary(c => c.Key, c => new ColumnNameHints(c.OriginalName).Tokens, StringComparer.Ordinal);

        var anchors = profile.Columns
            .Where(c => c.Type == DetectedType.Identifier
                && new ColumnNameHints(c.OriginalName).IdHint == IdHint.Strong
                && tokens[c.Key].Count >= 2)
            .Select(c => (c.Key, Prefix: tokens[c.Key].Take(tokens[c.Key].Count - 1).ToArray()))
            .ToList();

        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var column in profile.Columns.Where(c => IdentityTypes.Contains(c.Type)))
        {
            var columnTokens = tokens[column.Key];
            // The anchor with the longest matching prefix wins (Account_Owner_ID beats Account_ID for Account_Owner_Name).
            var anchor = anchors
                .Where(a => a.Key != column.Key && a.Prefix.Length < columnTokens.Count && columnTokens.Take(a.Prefix.Length).SequenceEqual(a.Prefix))
                .OrderByDescending(a => a.Prefix.Length)
                .Select(a => a.Key)
                .FirstOrDefault();
            if (anchor is not null)
            {
                if (!members.TryGetValue(anchor, out var list))
                    members[anchor] = list = [];
                list.Add(column.Key);
            }
        }

        return members.Select(m => new EntityGroup(m.Key, m.Value)).ToArray();
    }
}
