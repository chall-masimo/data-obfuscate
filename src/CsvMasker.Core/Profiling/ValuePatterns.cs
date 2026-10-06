using System.Text.RegularExpressions;

namespace CsvMasker.Core.Profiling;

/// <summary>Shape of a value that parses as a US-format number. The value itself stays a string.</summary>
internal readonly record struct NumberShape(bool IsInteger, int Scale, bool HasThousands, bool HasCurrency, bool HasParentheses);

/// <summary>Value-level pattern checks used by the profiler. US formats only in v1.</summary>
internal static partial class ValuePatterns
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\d{5}-\d{4}$")]
    private static partial Regex ZipPlus4Regex();

    // (555) 123-4567, 555-123-4567, 555.123.4567, +1 555 123 4567, optional extension.
    [GeneratedRegex(@"^(?:\+?1[\s.-]?)?(?:\(\d{3}\)\s?|\d{3}[\s.-])\d{3}[\s.-]\d{4}(?:\s*(?:x|ext\.?)\s*\d{1,6})?$", RegexOptions.IgnoreCase)]
    private static partial Regex FormattedPhoneRegex();

    [GeneratedRegex(@"^\+?[\d\s().-]+(?:\s*(?:x|ext\.?)\s*\d{1,6})?$", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneCharactersRegex();

    // Optional parentheses, sign and $ (either order), digits with optional thousands groups, optional fraction.
    [GeneratedRegex(@"^(?<open>\()?(?<sign>[-+])?(?<currency>\$)?(?<sign2>[-+])?(?<int>\d{1,3}(?:,\d{3})+|\d+)?(?:\.(?<frac>\d+))?(?<close>\))?$")]
    private static partial Regex NumberRegex();

    public static bool IsAllDigits(string value) =>
        value.Length > 0 && !value.AsSpan().ContainsAnyExceptInRange('0', '9');

    /// <summary>All digits, longer than one character, starting with 0.</summary>
    public static bool HasLeadingZero(string value) =>
        value.Length > 1 && value[0] == '0' && IsAllDigits(value);

    public static bool IsEmail(string value) => value.Contains('@') && EmailRegex().IsMatch(value);

    public static bool IsZip5(string value) => value.Length == 5 && IsAllDigits(value);

    public static bool IsZipPlus4(string value) => value.Length == 10 && ZipPlus4Regex().IsMatch(value);

    /// <summary>3–4 digits: a ZIP whose leading zeros were stripped upstream (e.g. by Excel).</summary>
    public static bool IsShortZip(string value) => value.Length is 3 or 4 && IsAllDigits(value);

    /// <summary>Phone with formatting. A bare run of digits is deliberately not a phone: it could be an ID.</summary>
    public static bool IsFormattedPhone(string value) => FormattedPhoneRegex().IsMatch(value);

    /// <summary>Only phone characters and 7–20 digits. Used when the column name says "phone".</summary>
    public static bool IsPhoneLike(string value)
    {
        if (!PhoneCharactersRegex().IsMatch(value))
            return false;
        int digits = 0;
        foreach (char c in value)
            if (char.IsAsciiDigit(c))
                digits++;
        return digits is >= 7 and <= 20;
    }

    public static bool TryParseNumber(string value, out NumberShape shape)
    {
        shape = default;
        var match = NumberRegex().Match(value.Trim());
        if (!match.Success)
            return false;

        var integer = match.Groups["int"];
        var fraction = match.Groups["frac"];
        bool parentheses = match.Groups["open"].Success;
        if (!integer.Success && !fraction.Success)
            return false;
        if (parentheses != match.Groups["close"].Success)
            return false;
        if (match.Groups["sign"].Success && match.Groups["sign2"].Success)
            return false;

        shape = new NumberShape(
            IsInteger: !fraction.Success,
            Scale: fraction.Success ? fraction.Length : 0,
            HasThousands: integer.Success && integer.ValueSpan.Contains(','),
            HasCurrency: match.Groups["currency"].Success,
            HasParentheses: parentheses);
        return true;
    }
}
