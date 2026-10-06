using System.Text;

namespace CsvMasker.Core.Profiling;

internal enum IdHint
{
    None,

    /// <summary>Name ends in <c>Code</c>: an identifier only if the values also look like one.</summary>
    Weak,

    /// <summary>Name ends in <c>ID</c>, <c>Key</c>, <c>No</c>, <c>Number</c>…</summary>
    Strong,
}

internal enum NameKind
{
    Person,
    Org,
}

/// <summary>
/// Hints taken from a column name. The name is split into lower-case tokens on separators,
/// camelCase and letter/digit boundaries: <c>CustomerID</c> → customer, id;
/// <c>ShipTo Address1</c> → ship, to, address, 1. A token ending in "name" is split off, so
/// <c>FIRSTNAME</c> → first, name.
/// </summary>
internal sealed class ColumnNameHints
{
    private static readonly HashSet<string> StrongIdTokens = ["id", "key", "no", "nbr", "num", "number", "guid", "uuid"];
    private static readonly HashSet<string> PersonTokens =
        ["first", "last", "full", "middle", "given", "surname", "rep", "contact", "owner", "manager", "employee", "patient", "physician"];
    private static readonly HashSet<string> OrgTokens =
        ["customer", "account", "facility", "hospital", "company", "vendor", "client", "supplier", "payer", "org", "organization"];
    private static readonly HashSet<string> CountTokens = ["qty", "quantity", "count", "cnt", "beds", "units"];
    private static readonly HashSet<string> MeasureTokens =
        ["amount", "amt", "revenue", "price", "cost", "margin", "total", "balance", "fee", "fees", "charge", "charges", "payment", "sales"];

    private readonly string _joined;

    public ColumnNameHints(string columnName)
    {
        Tokens = Tokenize(columnName ?? "");
        _joined = string.Concat(Tokens);
    }

    public IReadOnlyList<string> Tokens { get; }

    public string? LastToken => Tokens.Count > 0 ? Tokens[^1] : null;

    public bool IsZip => _joined.Contains("zip") || _joined.Contains("postal") || HasToken("postcode");

    public bool IsPhone => _joined.Contains("phone") || HasToken("fax", "mobile", "cell", "tel");

    /// <summary>Gates digit-only date formats such as yyyyMMdd, which would otherwise match IDs.</summary>
    public bool IsDateLike => _joined.Contains("date") || HasToken("dt", "day", "dob", "time", "timestamp");

    public bool IsState => HasToken("state", "province");

    public bool IsCity => HasToken("city", "town");

    public bool IsAddress => _joined.Contains("address") || HasToken("addr", "street");

    public bool IsCount => Tokens.Any(CountTokens.Contains) || HasSequence("number", "of") || HasSequence("num", "of");

    public bool IsMeasure => Tokens.Any(MeasureTokens.Contains);

    public bool IsFirstName => HasToken("first", "given", "middle");

    public bool IsLastName => HasToken("last", "surname");

    public bool IsHospital => HasToken("hospital", "facility");

    public IdHint IdHint => LastToken switch
    {
        null => IdHint.None,
        "code" => IdHint.Weak,
        var t when StrongIdTokens.Contains(t) => IdHint.Strong,
        _ => IdHint.None,
    };

    /// <summary>
    /// Person or organisation, decided by the qualifier closest before a "name" token:
    /// <c>Account_Owner_Name</c> → Person (owner), <c>Customer_Name</c> → Org (customer).
    /// Null when there's no "name" token or no recognised qualifier.
    /// </summary>
    public (NameKind Kind, string Qualifier)? NameKindHint
    {
        get
        {
            int nameIndex = -1;
            for (int i = Tokens.Count - 1; i >= 0 && nameIndex < 0; i--)
                if (Tokens[i] == "name")
                    nameIndex = i;

            for (int i = nameIndex - 1; i >= 0; i--)
            {
                if (PersonTokens.Contains(Tokens[i])) return (NameKind.Person, Tokens[i]);
                if (OrgTokens.Contains(Tokens[i])) return (NameKind.Org, Tokens[i]);
            }
            return null;
        }
    }

    public bool HasToken(params string[] tokens) => Tokens.Any(tokens.Contains);

    private bool HasSequence(string first, string second)
    {
        for (int i = 0; i + 1 < Tokens.Count; i++)
            if (Tokens[i] == first && Tokens[i + 1] == second)
                return true;
        return false;
    }

    internal static List<string> Tokenize(string name)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
                return;
            string token = current.ToString();
            current.Clear();
            if (token.Length > 4 && token.EndsWith("name", StringComparison.Ordinal))
            {
                tokens.Add(token[..^4]);
                tokens.Add("name");
            }
            else
            {
                tokens.Add(token);
            }
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                char prev = name[i - 1];
                bool boundary =
                    char.IsDigit(c) != char.IsDigit(prev)
                    || (char.IsUpper(c) && char.IsLower(prev))
                    || (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (boundary)
                    Flush();
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush();
        return tokens;
    }
}
