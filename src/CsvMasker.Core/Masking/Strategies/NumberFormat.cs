using System.Globalization;
using System.Text;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>
/// A US-format number with the formatting needed to write a new value the same way: sign,
/// currency symbol and its position, thousands separators, parentheses negatives, decimal
/// scale, a missing leading zero (".75"), and surrounding whitespace.
/// </summary>
internal readonly record struct NumberFormat(
    decimal Value,
    int Scale,
    bool Thousands,
    bool Currency,
    bool CurrencyBeforeSign,
    bool Parentheses,
    bool ExplicitPlus,
    bool OmitsLeadingZero,
    int ZeroPadWidth,
    string LeadingSpace,
    string TrailingSpace)
{
    public static bool TryParse(string text, out NumberFormat format)
    {
        format = default;
        var s = text.AsSpan();
        int start = 0, end = s.Length;
        while (start < end && char.IsWhiteSpace(s[start])) start++;
        while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
        var body = s[start..end];
        if (body.IsEmpty)
            return false;

        bool parentheses = body[0] == '(';
        if (parentheses)
        {
            if (body.Length < 3 || body[^1] != ')')
                return false;
            body = body[1..^1];
        }

        bool negative = false, plus = false, currency = false, currencyBeforeSign = false;
        for (int i = 0; i < 2 && !body.IsEmpty; i++)
        {
            if (body[0] == '$' && !currency)
            {
                currency = true;
                currencyBeforeSign = !negative && !plus;
                body = body[1..];
            }
            else if (body[0] is '-' or '+' && !negative && !plus)
            {
                negative = body[0] == '-';
                plus = body[0] == '+';
                body = body[1..];
            }
        }
        if (parentheses && (negative || plus))
            return false;

        // Digits with optional thousands groups, then an optional fraction.
        int dot = body.IndexOf('.');
        var integer = dot < 0 ? body : body[..dot];
        var fraction = dot < 0 ? ReadOnlySpan<char>.Empty : body[(dot + 1)..];
        if (dot >= 0 && (fraction.IsEmpty || fraction.ContainsAnyExceptInRange('0', '9')))
            return false;
        if (integer.IsEmpty && dot < 0)
            return false;

        bool thousands = integer.Contains(',');
        if (thousands ? !ValidThousands(integer) : integer.ContainsAnyExceptInRange('0', '9'))
            return false;

        var digits = new StringBuilder(integer.Length + fraction.Length + 2);
        foreach (char c in integer)
            if (c != ',') digits.Append(c);
        if (digits.Length == 0) digits.Append('0');
        if (!fraction.IsEmpty) digits.Append('.').Append(fraction);

        if (!decimal.TryParse(digits.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
            return false;

        format = new NumberFormat(
            negative || parentheses ? -value : value,
            fraction.Length,
            thousands,
            currency,
            currencyBeforeSign,
            parentheses,
            plus,
            OmitsLeadingZero: integer.IsEmpty,
            ZeroPadWidth: !thousands && integer.Length > 1 && integer[0] == '0' ? integer.Length : 0,
            LeadingSpace: text[..start],
            TrailingSpace: text[end..]);
        return true;
    }

    /// <summary>Writes <paramref name="value"/> in this number's format (rounded to its scale).</summary>
    public string Format(decimal value)
    {
        value = Math.Round(value, Scale, MidpointRounding.AwayFromZero);
        bool negative = value < 0;
        string digits = Math.Abs(value).ToString((Thousands ? "N" : "F") + Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (OmitsLeadingZero && digits.StartsWith("0.", StringComparison.Ordinal))
            digits = digits[1..];
        if (ZeroPadWidth > 0)
        {
            int integerDigits = digits.IndexOf('.') is var dot and >= 0 ? dot : digits.Length;
            if (integerDigits < ZeroPadWidth)
                digits = new string('0', ZeroPadWidth - integerDigits) + digits;
        }

        string currency = Currency ? "$" : "";
        string body;
        if (negative && Parentheses)
            body = $"({currency}{digits})";
        else
        {
            string sign = negative ? "-" : ExplicitPlus ? "+" : "";
            body = CurrencyBeforeSign ? currency + sign + digits : sign + currency + digits;
        }
        return LeadingSpace + body + TrailingSpace;
    }

    private static bool ValidThousands(ReadOnlySpan<char> integer)
    {
        int firstComma = integer.IndexOf(',');
        if (firstComma is < 1 or > 3)
            return false;
        for (int i = 0; i < integer.Length; i++)
        {
            bool commaPosition = (integer.Length - i) % 4 == 0;
            if (commaPosition != (integer[i] == ','))
                return false;
            if (!commaPosition && !char.IsAsciiDigit(integer[i]))
                return false;
        }
        return true;
    }
}
