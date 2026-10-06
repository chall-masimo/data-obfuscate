namespace CsvMasker.Core.Csv;

/// <summary>How a CSV file is physically written.</summary>
/// <param name="Encoding">Text encoding and BOM.</param>
/// <param name="Delimiter">Field separator: comma, tab, semicolon or pipe.</param>
/// <param name="LineEnding">
/// The header row's terminator. Records read from the source keep their own terminator; this is
/// only the default for records written without one.
/// </param>
public sealed record CsvDialect(CsvEncodingInfo Encoding, char Delimiter, string LineEnding);
