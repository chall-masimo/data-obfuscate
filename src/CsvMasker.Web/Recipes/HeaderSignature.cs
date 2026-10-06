using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CsvMasker.Web.Recipes;

/// <summary>
/// Identifies a file layout: SHA-256 (hex) of the ordered column names, each normalized
/// (trimmed, internal whitespace collapsed, lower-cased), joined with U+001F. Duplicate and blank
/// names are kept as they are, so the signature reflects the real layout.
/// </summary>
public static partial class HeaderSignature
{
    public static string Normalize(string name) =>
        Whitespace().Replace(name.Trim(), " ").ToLowerInvariant();

    public static string Compute(IEnumerable<string> names)
    {
        string joined = string.Join('\u001F', names.Select(Normalize));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
