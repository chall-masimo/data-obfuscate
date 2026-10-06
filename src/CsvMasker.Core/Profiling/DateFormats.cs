using System.Globalization;

namespace CsvMasker.Core.Profiling;

internal enum DateOrder
{
    None,
    MonthFirst,
    DayFirst,
}

internal sealed record DateFormat(string Pattern, DateOrder Order = DateOrder.None, bool DigitsOnly = false)
{
    public bool HasTime => Pattern.Contains('H') || Pattern.Contains('h');
}

/// <summary>Result of date detection for one column.</summary>
internal sealed record DateResult(bool IsDate, IReadOnlyList<string> Formats, bool HasTime, bool AmbiguousDayMonth, long Covered)
{
    public static DateResult None { get; } = new(false, [], false, false, 0);
}

/// <summary>
/// Tries every candidate format against each value, recording which formats matched as a bitmask,
/// then picks the smallest set of formats that covers the column (greedy set cover).
/// </summary>
internal sealed class DateMatcher
{
    // Order matters: on equal coverage the earlier format wins, so month-first (US) beats day-first.
    internal static readonly DateFormat[] Formats =
    [
        new("yyyy-MM-dd"),
        new("yyyy-MM-ddTHH:mm:ss"),
        new("yyyy-MM-ddTHH:mm:ss.FFFFFFF"),
        new("yyyy-MM-ddTHH:mm:ssK"),
        new("yyyy-MM-ddTHH:mm:ss.FFFFFFFK"),
        new("yyyy-MM-dd HH:mm:ss"),
        new("yyyy-MM-dd HH:mm:ss.FFFFFFF"),
        new("yyyy-MM-dd HH:mm"),
        new("yyyy/MM/dd"),
        new("M/d/yyyy", DateOrder.MonthFirst),
        new("M/d/yy", DateOrder.MonthFirst),
        new("M/d/yyyy h:mm tt", DateOrder.MonthFirst),
        new("M/d/yyyy h:mm:ss tt", DateOrder.MonthFirst),
        new("M/d/yyyy H:mm", DateOrder.MonthFirst),
        new("M/d/yyyy H:mm:ss", DateOrder.MonthFirst),
        new("M-d-yyyy", DateOrder.MonthFirst),
        new("d/M/yyyy", DateOrder.DayFirst),
        new("d/M/yy", DateOrder.DayFirst),
        new("d/M/yyyy h:mm tt", DateOrder.DayFirst),
        new("d/M/yyyy h:mm:ss tt", DateOrder.DayFirst),
        new("d/M/yyyy H:mm", DateOrder.DayFirst),
        new("d/M/yyyy H:mm:ss", DateOrder.DayFirst),
        new("d-M-yyyy", DateOrder.DayFirst),
        new("d-MMM-yyyy"),
        new("d-MMM-yy"),
        new("d MMM yyyy"),
        new("MMM d, yyyy"),
        new("MMMM d, yyyy"),
        new("yyyyMMdd", DigitsOnly: true),
    ];

    // Give up on a column once this many values have been seen and too few parse.
    private const int MinValuesBeforeGivingUp = 200;

    private readonly bool _allowDigitsOnly;
    private readonly double _requiredRatio;
    private readonly Dictionary<ulong, long> _maskCounts = [];
    private long _seen;
    private long _matched;
    private bool _gaveUp;

    public DateMatcher(bool allowDigitsOnly, double requiredRatio)
    {
        _allowDigitsOnly = allowDigitsOnly;
        _requiredRatio = requiredRatio;
    }

    public void Add(string value)
    {
        if (_gaveUp)
            return;

        _seen++;
        ulong mask = LooksDateLike(value) ? Match(value) : 0;
        if (mask != 0)
        {
            _matched++;
            _maskCounts[mask] = _maskCounts.GetValueOrDefault(mask) + 1;
        }

        // Non-date columns stop paying for ~30 parse attempts per value almost immediately.
        if (_seen >= MinValuesBeforeGivingUp && _matched < _seen * _requiredRatio)
        {
            _gaveUp = true;
            _maskCounts.Clear();
        }
    }

    public DateResult Evaluate(long nonBlankCount)
    {
        if (_gaveUp || nonBlankCount == 0 || _matched < nonBlankCount * _requiredRatio)
            return DateResult.None;

        var chosen = new List<int>();
        ulong chosenBits = 0;
        long covered = 0;
        while (covered < _matched)
        {
            int best = -1;
            long bestGain = 0;
            for (int f = 0; f < Formats.Length; f++)
            {
                ulong bit = 1UL << f;
                if ((chosenBits & bit) != 0)
                    continue;
                long gain = 0;
                foreach (var (mask, count) in _maskCounts)
                    if ((mask & bit) != 0 && (mask & chosenBits) == 0)
                        gain += count;
                if (gain > bestGain)
                {
                    best = f;
                    bestGain = gain;
                }
            }

            if (best < 0)
                break;
            chosen.Add(best);
            chosenBits |= 1UL << best;
            covered += bestGain;
        }

        bool ambiguous = chosen.Any(f => Formats[f].Order == DateOrder.MonthFirst) && !AnyValueIsMonthFirstOnly();
        return new DateResult(
            IsDate: true,
            Formats: chosen.Select(f => Formats[f].Pattern).ToArray(),
            HasTime: chosen.Any(f => Formats[f].HasTime),
            AmbiguousDayMonth: ambiguous,
            Covered: covered);
    }

    private bool AnyValueIsMonthFirstOnly()
    {
        ulong monthBits = Bits(DateOrder.MonthFirst), dayBits = Bits(DateOrder.DayFirst);
        return _maskCounts.Keys.Any(mask => (mask & monthBits) != 0 && (mask & dayBits) == 0);
    }

    private static ulong Bits(DateOrder order)
    {
        ulong bits = 0;
        for (int f = 0; f < Formats.Length; f++)
            if (Formats[f].Order == order)
                bits |= 1UL << f;
        return bits;
    }

    private ulong Match(string value)
    {
        ulong mask = 0;
        for (int f = 0; f < Formats.Length; f++)
        {
            if (Formats[f].DigitsOnly && !_allowDigitsOnly)
                continue;
            if (DateTime.TryParseExact(value, Formats[f].Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                mask |= 1UL << f;
        }
        return mask;
    }

    private static bool LooksDateLike(string value) =>
        value.Length is >= 6 and <= 40 && char.IsLetterOrDigit(value[0]) && value.AsSpan().ContainsAnyInRange('0', '9');
}
