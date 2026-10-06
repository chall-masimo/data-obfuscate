namespace CsvMasker.Core.Csv;

/// <summary>
/// One parsed CSV record, with everything needed to write it back byte-for-byte.
/// </summary>
/// <remarks>
/// CSV has no null. By convention here an <b>unquoted</b> empty field is null and a quoted empty
/// field (<c>""</c>) is an empty string; see <see cref="IsNull"/>.
/// </remarks>
public sealed class CsvRecord
{
    public CsvRecord(
        IReadOnlyList<string> values,
        IReadOnlyList<bool> wasQuoted,
        string lineEnding,
        long recordNumber = 0,
        bool isBlankLine = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(wasQuoted);
        ArgumentNullException.ThrowIfNull(lineEnding);
        if (wasQuoted.Count != values.Count)
            throw new ArgumentException("wasQuoted must have one entry per value.", nameof(wasQuoted));

        Values = values;
        WasQuoted = wasQuoted;
        LineEnding = lineEnding;
        RecordNumber = recordNumber;
        IsBlankLine = isBlankLine;
    }

    /// <summary>Field values, always as strings: nothing is parsed or coerced.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Whether each field was quoted in the source.</summary>
    public IReadOnlyList<bool> WasQuoted { get; }

    /// <summary>"\r\n", "\n", "\r", or "" for a final record with no trailing newline.</summary>
    public string LineEnding { get; }

    /// <summary>1-based position in the file; the header is record 1. 0 for records built in code.</summary>
    public long RecordNumber { get; }

    /// <summary>
    /// An empty line in a file with more than one column. It is written back verbatim but is not a
    /// data row. (In a one-column file an empty line is a row holding a null value.)
    /// </summary>
    public bool IsBlankLine { get; }

    public bool IsNull(int index) => !WasQuoted[index] && Values[index].Length == 0;

    /// <summary>A copy with new values but the same quoting, line ending and position.</summary>
    public CsvRecord WithValues(IReadOnlyList<string> values) =>
        new(values, WasQuoted, LineEnding, RecordNumber, IsBlankLine);
}
