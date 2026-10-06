namespace CsvMasker.Core.Profiling;

/// <summary>
/// Streaming statistics for one column, fed one value at a time. Memory is bounded: the distinct
/// counter caps itself, the date matcher keeps only per-pattern counts, and just a handful of
/// sample values are retained.
/// </summary>
internal sealed class ColumnAccumulator
{
    private readonly int _sampleValueCount;
    private readonly List<string> _samples = [];
    private long _totalLength;

    public ColumnAccumulator(string originalName, ProfileOptions options)
    {
        Hints = new ColumnNameHints(originalName);
        _sampleValueCount = options.SampleValueCount;
        Distinct = new DistinctCounter(options.ExactDistinctLimit);
        Dates = new DateMatcher(allowDigitsOnly: Hints.IsDateLike, options.Thresholds.PatternMatchRatio);
    }

    public ColumnNameHints Hints { get; }
    public DistinctCounter Distinct { get; }
    public DateMatcher Dates { get; }

    public long RowCount { get; private set; }

    /// <summary>Unquoted empty fields.</summary>
    public long NullCount { get; private set; }

    /// <summary>Quoted empty (<c>""</c>) or whitespace-only fields.</summary>
    public long BlankCount { get; private set; }

    /// <summary>Everything else; all the remaining counts are out of this.</summary>
    public long ValueCount { get; private set; }

    public int MinLength { get; private set; }
    public int MaxLength { get; private set; }
    public double AverageLength => ValueCount == 0 ? 0 : (double)_totalLength / ValueCount;

    public long AllDigitsCount { get; private set; }
    public long LeadingZeroCount { get; private set; }
    public long DigitNoWhitespaceCount { get; private set; }
    public long WhitespaceCount { get; private set; }

    public long NumericCount { get; private set; }
    public long IntegerCount { get; private set; }
    public int MaxScale { get; private set; }
    public bool UsesThousands { get; private set; }
    public bool UsesCurrency { get; private set; }
    public bool UsesParentheses { get; private set; }

    public long EmailCount { get; private set; }
    public long Zip5Count { get; private set; }
    public long ZipPlus4Count { get; private set; }
    public long ShortZipCount { get; private set; }
    public long FormattedPhoneCount { get; private set; }
    public long PhoneLikeCount { get; private set; }
    public long StateCount { get; private set; }

    /// <summary>First distinct non-blank values. Sensitive: kept in memory only, never logged.</summary>
    public IReadOnlyList<string> Samples => _samples;

    public void Add(string value, bool isNull)
    {
        RowCount++;
        if (isNull)
        {
            NullCount++;
            return;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            BlankCount++;
            return;
        }

        ValueCount++;
        Distinct.Add(value);
        if (_samples.Count < _sampleValueCount && !_samples.Contains(value))
            _samples.Add(value);

        MinLength = ValueCount == 1 ? value.Length : Math.Min(MinLength, value.Length);
        MaxLength = Math.Max(MaxLength, value.Length);
        _totalLength += value.Length;

        bool allDigits = ValuePatterns.IsAllDigits(value);
        bool hasDigit = allDigits || value.AsSpan().ContainsAnyInRange('0', '9');
        bool hasWhitespace = value.AsSpan().ContainsAny(' ', '\t');
        if (allDigits) AllDigitsCount++;
        if (ValuePatterns.HasLeadingZero(value)) LeadingZeroCount++;
        if (hasDigit && !hasWhitespace) DigitNoWhitespaceCount++;
        if (hasWhitespace) WhitespaceCount++;

        if (ValuePatterns.TryParseNumber(value, out var number))
        {
            NumericCount++;
            if (number.IsInteger) IntegerCount++;
            MaxScale = Math.Max(MaxScale, number.Scale);
            UsesThousands |= number.HasThousands;
            UsesCurrency |= number.HasCurrency;
            UsesParentheses |= number.HasParentheses;
        }

        if (ValuePatterns.IsEmail(value)) EmailCount++;
        if (ValuePatterns.IsZip5(value)) Zip5Count++;
        else if (ValuePatterns.IsZipPlus4(value)) ZipPlus4Count++;
        else if (ValuePatterns.IsShortZip(value)) ShortZipCount++;

        if (hasDigit)
        {
            if (ValuePatterns.IsFormattedPhone(value)) FormattedPhoneCount++;
            if (ValuePatterns.IsPhoneLike(value)) PhoneLikeCount++;
        }
        else if (UsStates.IsState(value))
        {
            StateCount++;
        }

        Dates.Add(value);
    }
}
