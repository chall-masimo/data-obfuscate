using CsvMasker.Core.Csv;

namespace CsvMasker.Core.Tests;

/// <summary>Builds CSV inputs in code (no binary fixtures) and runs them through the reader and writer.</summary>
internal static class TestFiles
{
    public static readonly CsvEncodingInfo Utf8Bom = CsvEncodingInfo.Utf8(withBom: true);
    public static readonly CsvEncodingInfo Utf8 = CsvEncodingInfo.Utf8(withBom: false);
    public static readonly CsvEncodingInfo Win1252 = CsvEncodingInfo.Windows1252;
    public static readonly CsvEncodingInfo Utf16LeBom = CsvEncodingInfo.Utf16(bigEndian: false, withBom: true);
    public static readonly CsvEncodingInfo Utf16BeBom = CsvEncodingInfo.Utf16(bigEndian: true, withBom: true);
    public static readonly CsvEncodingInfo Utf16Le = CsvEncodingInfo.Utf16(bigEndian: false, withBom: false);
    public static readonly CsvEncodingInfo Utf16Be = CsvEncodingInfo.Utf16(bigEndian: true, withBom: false);
    public static readonly CsvEncodingInfo Utf32LeBom = CsvEncodingInfo.Utf32(bigEndian: false);

    public static byte[] Encode(string text, CsvEncodingInfo encoding) =>
        [.. encoding.Preamble, .. encoding.Encoding.GetBytes(text)];

    /// <summary>Detects the dialect, reads every record and writes them back.</summary>
    public static byte[] RoundTrip(byte[] input)
    {
        using var source = new MemoryStream(input, writable: false);
        var dialect = CsvDialectDetector.Detect(source);
        using var reader = new CsvReader(source, dialect, leaveOpen: true);

        using var output = new MemoryStream();
        using (var writer = new CsvWriter(output, dialect, leaveOpen: true))
        {
            writer.WriteRecord(reader.Header.Record);
            while (reader.TryRead(out var record))
                writer.WriteRecord(record);
        }
        return output.ToArray();
    }

    public static (CsvHeader Header, List<CsvRecord> Records) ReadAll(string text, CsvReaderOptions? options = null)
    {
        using var source = new MemoryStream(Encode(text, Utf8), writable: false);
        var dialect = CsvDialectDetector.Detect(source);
        using var reader = new CsvReader(source, dialect, options);
        var records = new List<CsvRecord>();
        while (reader.TryRead(out var record))
            records.Add(record);
        return (reader.Header, records);
    }

    public static CsvDialect Dialect(CsvEncodingInfo encoding, char delimiter = ',', string lineEnding = "\r\n") =>
        new(encoding, delimiter, lineEnding);
}
