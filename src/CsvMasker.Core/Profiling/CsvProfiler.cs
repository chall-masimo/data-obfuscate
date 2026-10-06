using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Profiling;

/// <summary>
/// Reads up to <see cref="ProfileOptions.SampleRows"/> data rows and profiles every column.
/// Rows with the wrong number of fields are skipped and reported by record number; other
/// format errors (e.g. an unclosed quote) stop the profile with a <see cref="CsvFormatException"/>.
/// </summary>
public static class CsvProfiler
{
    /// <summary>Detects the dialect of a seekable stream and profiles it.</summary>
    public static FileProfile Profile(Stream stream, ProfileOptions? options = null)
    {
        options ??= ProfileOptions.Default;
        var dialect = CsvDialectDetector.Detect(stream);
        using var reader = new CsvReader(stream, dialect, options.Reader, leaveOpen: true);
        return Profile(reader, dialect, options);
    }

    /// <summary>Profiles from an open reader, which must not have read any data rows yet.</summary>
    public static FileProfile Profile(CsvReader reader, CsvDialect dialect, ProfileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(dialect);
        options ??= ProfileOptions.Default;

        var header = reader.Header;
        var columns = new ColumnAccumulator[header.Count];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new ColumnAccumulator(header.OriginalNames[i], options);

        long rows = 0, blankLines = 0, malformed = 0;
        var malformedNumbers = new List<long>();
        bool reachedEnd = false;

        while (true)
        {
            CsvRecord? record;
            try
            {
                if (!reader.TryRead(out record))
                {
                    reachedEnd = true;
                    break;
                }
            }
            catch (CsvFormatException ex) when (ex.Kind == CsvErrorKind.FieldCountMismatch)
            {
                malformed++;
                if (malformedNumbers.Count < options.MaxMalformedRowsListed)
                    malformedNumbers.Add(ex.RecordNumber);
                continue;
            }

            if (record.IsBlankLine)
            {
                blankLines++;
                continue;
            }

            if (rows == options.SampleRows)
                break; // there is at least one more row: the sample is not the whole file

            rows++;
            for (int i = 0; i < columns.Length; i++)
                columns[i].Add(record.Values[i], record.IsNull(i));
        }

        var profiles = new ColumnProfile[columns.Length];
        for (int i = 0; i < columns.Length; i++)
            profiles[i] = BuildProfile(i, header.Keys[i], header.OriginalNames[i], columns[i], reachedEnd, options);

        return new FileProfile
        {
            Dialect = dialect,
            Header = header,
            RowsProfiled = rows,
            BlankLines = blankLines,
            ReachedEndOfFile = reachedEnd,
            MalformedRowCount = malformed,
            MalformedRecordNumbers = malformedNumbers,
            Columns = profiles,
        };
    }

    /// <summary>Profiles a single column from raw values; null means a CSV null (unquoted empty). For tests.</summary>
    internal static ColumnProfile ProfileColumn(string name, IEnumerable<string?> values, ProfileOptions? options = null)
    {
        options ??= ProfileOptions.Default;
        var column = new ColumnAccumulator(name, options);
        foreach (var value in values)
            column.Add(value ?? "", isNull: value is null);
        return BuildProfile(0, name, name, column, reachedEnd: true, options);
    }

    private static ColumnProfile BuildProfile(int index, string key, string originalName, ColumnAccumulator c, bool reachedEnd, ProfileOptions options)
    {
        var detection = TypeDetector.Detect(c, options.Thresholds);
        bool isNumeric = c.ValueCount > 0 && (double)c.NumericCount / c.ValueCount >= options.Thresholds.PatternMatchRatio;

        return new ColumnProfile
        {
            Index = index,
            Key = key,
            OriginalName = originalName,
            Type = detection.Type,
            Reason = detection.Reason,
            Warnings = detection.Warnings,
            Suggestion = StrategySuggester.Suggest(detection.Type, c.Hints),
            RowCount = c.RowCount,
            NullCount = c.NullCount,
            BlankCount = c.BlankCount,
            ValueCount = c.ValueCount,
            DistinctCount = c.Distinct.Count,
            DistinctIsEstimate = !c.Distinct.IsExact || !reachedEnd,
            MinLength = c.MinLength,
            MaxLength = c.MaxLength,
            AverageLength = c.AverageLength,
            HasLeadingZeros = c.LeadingZeroCount > 0,
            IsNumeric = isNumeric,
            IsIntegerOnly = isNumeric && c.IntegerCount == c.NumericCount,
            MaxDecimalScale = c.MaxScale,
            UsesThousandsSeparators = c.UsesThousands,
            UsesCurrencySymbol = c.UsesCurrency,
            UsesParenthesesNegatives = c.UsesParentheses,
            DateFormats = detection.Dates.Formats,
            HasTimeComponent = detection.Dates.HasTime,
            SampleValues = c.Samples.ToArray(),
        };
    }
}
