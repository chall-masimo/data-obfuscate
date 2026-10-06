using System.Globalization;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>
/// Shifts dates by a whole number of days. Only the year, month and day characters of the source
/// string are rewritten (same widths, same month-name casing); time, fraction and offset stay
/// byte-for-byte, so each value keeps its exact source format.
/// </summary>
internal sealed class DateShiftStrategy : IMaskingStrategy
{
    private readonly string[] _formats;
    private readonly int _offsetDays;

    public DateShiftStrategy(SeedSource seeds, string domain, DateShiftOptions options)
    {
        _formats = (options.Formats ?? [])
            .Concat(DateMatcher.Formats.Select(f => f.Pattern))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var random = seeds.Random(domain, SeedSource.GlobalValue);
        _offsetDays = Offset(ref random, options);
    }

    public bool IsMapping => false;

    internal int OffsetDays => _offsetDays;

    public string? Mask(in MaskInput input, int attempt)
    {
        string value = input.Value;
        foreach (var format in _formats)
        {
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                continue;
            if (DateComponents.TryLocate(value, format, out var parts) && parts.TryShift(value, _offsetDays, out var shifted))
                return shifted;
        }
        return null;
    }

    internal static int Offset(ref SeededRandom random, DateShiftOptions options)
    {
        int sign = random.Next(2) == 0 ? -1 : 1;
        if (options.KeepWeekday)
            return sign * 7 * random.Next(1, options.MaxDays / 7 + 1);
        return sign * random.Next(1, options.MaxDays + 1);
    }
}

/// <summary>Where the year, month and day sit inside a date string, for a given .NET format.</summary>
internal readonly record struct DateComponents(
    int YearStart, int YearLength,
    int MonthStart, int MonthLength, bool MonthIsName,
    int DayStart, int DayLength)
{
    /// <summary>Walks the format and the value together. Only the specifiers used in <see cref="DateMatcher.Formats"/> are supported.</summary>
    public static bool TryLocate(string value, string format, out DateComponents parts)
    {
        parts = default;
        int yearStart = -1, yearLength = 0, monthStart = -1, monthLength = 0, dayStart = -1, dayLength = 0;
        bool monthIsName = false;
        int v = 0;

        for (int f = 0; f < format.Length;)
        {
            char spec = format[f];
            int count = 1;
            while (f + count < format.Length && format[f + count] == spec) count++;

            int start = v;
            switch (spec)
            {
                case 'y':
                    v += Digits(value, v, count, count);
                    if (v - start != count) return false;
                    (yearStart, yearLength) = (start, count);
                    break;
                case 'M' when count >= 3:
                    while (v < value.Length && char.IsLetter(value[v])) v++;
                    if (v == start) return false;
                    (monthStart, monthLength, monthIsName) = (start, v - start, true);
                    break;
                case 'M':
                case 'd':
                case 'H':
                case 'h':
                case 'm':
                case 's':
                    v += Digits(value, v, count, 2);
                    if (v == start) return false;
                    if (spec == 'M') (monthStart, monthLength) = (start, v - start);
                    if (spec == 'd') (dayStart, dayLength) = (start, v - start);
                    break;
                case 'F':
                case 'f':
                    v += Digits(value, v, 0, count);
                    break;
                case 't':
                    while (v < value.Length && char.IsLetter(value[v])) v++;
                    break;
                case 'K':
                    if (v < value.Length && value[v] == 'Z') v++;
                    else if (v < value.Length && value[v] is '+' or '-') v = Math.Min(value.Length, v + 6);
                    break;
                case '.' when f + 1 < format.Length && format[f + 1] == 'F' && (v >= value.Length || value[v] != '.'):
                    // ".FFF" with no fraction present: .NET accepts it, so skip the dot too.
                    count = 1;
                    break;
                default:
                    // Literal characters must match one-for-one.
                    for (int i = 0; i < count; i++, v++)
                        if (v >= value.Length || value[v] != spec) return false;
                    break;
            }
            f += count;
        }

        if (v != value.Length || yearStart < 0 || monthStart < 0 || dayStart < 0)
            return false;
        parts = new DateComponents(yearStart, yearLength, monthStart, monthLength, monthIsName, dayStart, dayLength);
        return true;
    }

    public bool TryShift(string value, int days, out string shifted)
    {
        shifted = value;
        var culture = CultureInfo.InvariantCulture.DateTimeFormat;
        var yearText = value.AsSpan(YearStart, YearLength);
        int year = int.Parse(yearText, CultureInfo.InvariantCulture);
        if (YearLength == 2)
            year = CultureInfo.InvariantCulture.Calendar.ToFourDigitYear(year);

        int month;
        if (MonthIsName)
        {
            string name = value.Substring(MonthStart, MonthLength);
            month = 1 + Array.FindIndex(MonthLength == 3 ? culture.AbbreviatedMonthNames : culture.MonthNames,
                m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
            if (month == 0) return false;
        }
        else
        {
            month = int.Parse(value.AsSpan(MonthStart, MonthLength), CultureInfo.InvariantCulture);
        }
        int day = int.Parse(value.AsSpan(DayStart, DayLength), CultureInfo.InvariantCulture);

        DateOnly date;
        try
        {
            date = new DateOnly(year, month, day).AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        string monthText = MonthIsName
            ? FakeStrategy.CopyCase(value.Substring(MonthStart, MonthLength),
                (MonthLength == 3 ? culture.AbbreviatedMonthNames : culture.MonthNames)[date.Month - 1])
            : Number(date.Month, MonthLength);

        var replacements = new[]
        {
            (Start: YearStart, Length: YearLength, Text: YearLength == 2 ? (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture) : date.Year.ToString("D4", CultureInfo.InvariantCulture)),
            (Start: MonthStart, Length: MonthLength, Text: monthText),
            (Start: DayStart, Length: DayLength, Text: Number(date.Day, DayLength)),
        };

        var result = value;
        foreach (var r in replacements.OrderByDescending(r => r.Start))
            result = string.Concat(result.AsSpan(0, r.Start), r.Text, result.AsSpan(r.Start + r.Length));
        shifted = result;
        return true;
    }

    /// <summary>Zero-padded to 2 when the source was 2 digits; unpadded when it was 1.</summary>
    private static string Number(int n, int sourceWidth) =>
        n.ToString(sourceWidth == 2 ? "D2" : "D", CultureInfo.InvariantCulture);

    private static int Digits(string value, int start, int min, int max)
    {
        int n = 0;
        while (n < max && start + n < value.Length && char.IsAsciiDigit(value[start + n])) n++;
        return n >= min ? n : 0;
    }
}
