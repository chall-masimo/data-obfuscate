using System.Globalization;

namespace CsvMasker.Core.Profiling;

internal sealed record Detection(DetectedType Type, string Reason, IReadOnlyList<string> Warnings, DateResult Dates);

/// <summary>
/// Applies the detection rules in a fixed precedence order; the first match wins. "Matches" means
/// at least <see cref="DetectionThresholds.PatternMatchRatio"/> of non-blank values. Reasons and
/// warnings mention column-name tokens and percentages, never cell values.
/// </summary>
internal static class TypeDetector
{
    private static readonly HashSet<(string, string)> BooleanPairs =
        [("N", "Y"), ("NO", "YES"), ("F", "T"), ("FALSE", "TRUE"), ("0", "1")];

    public static Detection Detect(ColumnAccumulator c, DetectionThresholds t)
    {
        var warnings = new List<string>();
        long n = c.ValueCount;
        if (n == 0)
            return Result(DetectedType.Empty, "no values in the sample");

        var hints = c.Hints;
        double ratio = (double)c.Distinct.Count / n;
        bool Matches(long count) => (double)count / n >= t.PatternMatchRatio;
        string Pct(long count) => ((double)count / n).ToString("P0", CultureInfo.InvariantCulture);
        bool numeric = Matches(c.NumericCount);
        bool integersOnly = numeric && c.IntegerCount == c.NumericCount;

        Detection Result(DetectedType type, string reason, DateResult? dates = null) =>
            new(type, reason, warnings, dates ?? DateResult.None);

        // 1. Email
        if (Matches(c.EmailCount))
            return Result(DetectedType.Email, $"{Pct(c.EmailCount)} of values are email addresses");

        // 2. Zip
        long zipShaped = c.Zip5Count + c.ZipPlus4Count + c.ShortZipCount;
        if ((hints.IsZip && Matches(zipShaped)) || Matches(c.ZipPlus4Count))
        {
            if (c.ShortZipCount > 0)
                warnings.Add($"{Pct(c.ShortZipCount)} of ZIPs have 3–4 digits: leading zeros may have been stripped upstream.");
            return Result(DetectedType.Zip, hints.IsZip
                ? "name suggests a ZIP/postal code and values are ZIP-shaped"
                : $"{Pct(c.ZipPlus4Count)} of values are ZIP+4");
        }

        // 3. Phone (bare digit runs only count when the name says phone)
        if (hints.IsPhone && Matches(c.PhoneLikeCount))
            return Result(DetectedType.Phone, "name suggests a phone number and values are phone-shaped");
        if (Matches(c.FormattedPhoneCount))
            return Result(DetectedType.Phone, $"{Pct(c.FormattedPhoneCount)} of values are formatted phone numbers");

        // 4. Date / DateTime
        var dates = c.Dates.Evaluate(n);
        if (dates.IsDate)
        {
            if (dates.AmbiguousDayMonth)
                warnings.Add("Every value fits both month/day and day/month order; assumed month/day (US).");
            return Result(
                dates.HasTime ? DetectedType.DateTime : DetectedType.Date,
                $"{Pct(dates.Covered)} of values parse as {string.Join(" or ", dates.Formats)}",
                dates);
        }

        // 5. Boolean
        if (c.Distinct.ExactValues is { Count: <= 4 } values)
        {
            var folded = values.Select(v => v.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (folded.Length == 2 && BooleanPairs.Contains((folded[0], folded[1])))
                return Result(DetectedType.Boolean, "two distinct values forming a yes/no pair");
        }

        // 6. State
        if (Matches(c.StateCount) && (hints.IsState || c.Distinct.Count >= 5))
            return Result(DetectedType.State, $"{Pct(c.StateCount)} of values are US state codes or names");

        // 7. Identifier: never treated as a measure, even when numeric
        switch (hints.IdHint)
        {
            case IdHint.Strong:
                return Result(DetectedType.Identifier, $"name ends with '{hints.LastToken}'");
            case IdHint.Weak when ratio > t.IdentifierRatio || c.LeadingZeroCount > 0:
                return Result(DetectedType.Identifier, "name ends with 'code' and values are high-cardinality or zero-padded");
        }
        if (Matches(c.AllDigitsCount) && c.LeadingZeroCount > 0)
            return Result(DetectedType.Identifier, "digit codes with leading zeros");
        // Value-only identifier rules yield to a count/measure name hint (Number_of_Beds, Amount).
        bool quantityHint = hints.IsCount || hints.IsMeasure;
        bool plainIntegersOrText = c.NumericCount == 0 || (c.IntegerCount == c.NumericCount && !c.UsesThousands && !c.UsesCurrency);
        if (!quantityHint && c.MinLength == c.MaxLength && c.MinLength >= 4 && Matches(c.DigitNoWhitespaceCount)
            && plainIntegersOrText && ratio > t.IdentifierRatio)
            return Result(DetectedType.Identifier, "fixed-width codes with high cardinality");
        if (!quantityHint && integersOnly && !c.UsesThousands && !c.UsesCurrency && ratio > t.NearUniqueRatio)
            return Result(DetectedType.Identifier, "near-unique integers");

        // 8. Person / organisation names
        if (hints.NameKindHint is { } nameHint)
            return Result(
                nameHint.Kind == NameKind.Person ? DetectedType.PersonName : DetectedType.OrgName,
                $"name hint '{nameHint.Qualifier}' + 'name'");

        // 9. City / address (city first: "Address_City" is a city)
        if (hints.IsCity)
            return Result(DetectedType.City, "name suggests a city");
        if (hints.IsAddress)
            return Result(DetectedType.Address, "name suggests a street address");

        // 10–11. Count / hinted measure
        if (integersOnly && hints.IsCount)
            return Result(DetectedType.Count, "integers and the name suggests a count");
        if (numeric && (hints.IsMeasure || c.MaxScale > 0 || c.UsesCurrency))
            return Result(DetectedType.Measure, hints.IsMeasure ? "numeric and the name suggests an amount" : "decimal or currency values");

        // 12. Categorical
        if (c.Distinct.Count <= t.CategoricalMaxDistinct || ratio < t.CategoricalMaxRatio)
            return Result(DetectedType.Categorical, $"low cardinality ({(c.Distinct.IsExact ? "" : "about ")}{c.Distinct.Count:N0} distinct values)");

        // 13. Remaining numeric
        if (numeric)
            return Result(DetectedType.Measure, "numeric values");

        // 14. Free text
        if (c.AverageLength > t.FreeTextMinAverageLength)
            return Result(DetectedType.FreeText, $"long values (average {c.AverageLength:F0} characters)");
        if (ratio > t.IdentifierRatio && c.WhitespaceCount >= n / 2.0)
            return Result(DetectedType.FreeText, "high-cardinality text with spaces");

        // 15. Fallback
        return Result(DetectedType.Text, "no detection rule matched");
    }
}
