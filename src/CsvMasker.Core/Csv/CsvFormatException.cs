namespace CsvMasker.Core.Csv;

public enum CsvErrorKind
{
    MissingHeader,
    UnclosedQuote,
    TextAfterClosingQuote,
    FieldCountMismatch,
    RecordTooLarge,
    UndecodableInput,
    UnencodableOutput,
}

/// <summary>
/// A CSV read or write failure. The message identifies the position (1-based record and field
/// numbers, where the header is record 1) but never contains cell content, so it is safe to log.
/// No inner exception is attached, because encoding exceptions include the offending bytes.
/// </summary>
public sealed class CsvFormatException : Exception
{
    private CsvFormatException(CsvErrorKind kind, long recordNumber, int? fieldNumber, string message)
        : base(message)
    {
        Kind = kind;
        RecordNumber = recordNumber;
        FieldNumber = fieldNumber;
    }

    public CsvErrorKind Kind { get; }

    /// <summary>1-based record number; the header is record 1. 0 when there is no record.</summary>
    public long RecordNumber { get; }

    /// <summary>1-based field number, when the error is tied to one field.</summary>
    public int? FieldNumber { get; }

    internal static CsvFormatException MissingHeader() =>
        new(CsvErrorKind.MissingHeader, 0, null, "The file is empty: a header row is required.");

    internal static CsvFormatException UnclosedQuote(long record, int field) =>
        new(CsvErrorKind.UnclosedQuote, record, field,
            $"Record {record}, field {field}: a quoted field is not closed before the end of the file.");

    internal static CsvFormatException TextAfterClosingQuote(long record, int field) =>
        new(CsvErrorKind.TextAfterClosingQuote, record, field,
            $"Record {record}, field {field}: unexpected characters after a closing quote.");

    internal static CsvFormatException FieldCountMismatch(long record, int expected, int actual) =>
        new(CsvErrorKind.FieldCountMismatch, record, null,
            $"Record {record}: expected {expected} fields but found {actual}.");

    internal static CsvFormatException RecordTooLarge(long record, int maxChars) =>
        new(CsvErrorKind.RecordTooLarge, record, null,
            $"Record {record} is longer than the limit of {maxChars:N0} characters.");

    internal static CsvFormatException UndecodableInput(long record, string encodingName) =>
        new(CsvErrorKind.UndecodableInput, record, null,
            $"The input could not be decoded as {encodingName} at or after record {record}.");

    internal static CsvFormatException UnencodableOutput(long record, string encodingName) =>
        new(CsvErrorKind.UnencodableOutput, record, null,
            $"Record {record} contains a character that cannot be written as {encodingName}.");
}
