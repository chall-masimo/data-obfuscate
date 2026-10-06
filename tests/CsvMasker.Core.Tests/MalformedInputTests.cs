using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

/// <summary>
/// Each input contains "SECRET"; the exception (message and full ToString, which is what a logger
/// would capture) must never contain it.
/// </summary>
public class MalformedInputTests
{
    private const string Secret = "SECRET";

    [Fact]
    public void Unclosed_quote()
    {
        var ex = ReadToEnd("a,b\r\n1,\"SECRET-123\r\n2,3\r\n");

        AssertError(ex, CsvErrorKind.UnclosedQuote, record: 2, field: 2);
    }

    [Fact]
    public void Text_after_closing_quote()
    {
        var ex = ReadToEnd("a,b\r\n\"SECRET\"-123,2\r\n");

        AssertError(ex, CsvErrorKind.TextAfterClosingQuote, record: 2, field: 1);
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\nSECRET-123\r\n")]
    [InlineData("a,b\r\n1,2\r\nSECRET,1,2\r\n")]
    public void Field_count_mismatch(string text)
    {
        var ex = ReadToEnd(text);

        AssertError(ex, CsvErrorKind.FieldCountMismatch, record: 3, field: null);
    }

    [Fact]
    public void Record_too_large()
    {
        var ex = ReadToEnd("a,b\r\nSECRET-123456789,2\r\n", new CsvReaderOptions { MaxRecordChars = 10 });

        AssertError(ex, CsvErrorKind.RecordTooLarge, record: 2, field: null);
    }

    [Fact]
    public void Runaway_quote_is_stopped_by_record_limit()
    {
        string text = "a,b\r\n\"" + string.Concat(Enumerable.Repeat("SECRET,x\r\n", 1_000));

        var ex = ReadToEnd(text, new CsvReaderOptions { MaxRecordChars = 500 });

        AssertError(ex, CsvErrorKind.RecordTooLarge, record: 2, field: null);
    }

    [Fact]
    public void Undecodable_input()
    {
        byte[] input = [.. "a,b\r\nSECRET-"u8, 0xFF, .. ",1\r\n"u8];
        using var stream = new MemoryStream(input);

        var ex = Assert.Throws<CsvFormatException>(() =>
        {
            using var reader = new CsvReader(stream, Dialect(Utf8));
            while (reader.TryRead(out _)) { }
        });

        Assert.Equal(CsvErrorKind.UndecodableInput, ex.Kind);
        Assert.DoesNotContain(Secret, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
    public void Missing_header(byte[] input)
    {
        using var stream = new MemoryStream(input);
        var dialect = CsvDialectDetector.Detect(stream);

        var ex = Assert.Throws<CsvFormatException>(() => new CsvReader(stream, dialect));

        Assert.Equal(CsvErrorKind.MissingHeader, ex.Kind);
    }

    private static CsvFormatException ReadToEnd(string text, CsvReaderOptions? options = null) =>
        Assert.Throws<CsvFormatException>(() => ReadAll(text, options));

    private static void AssertError(CsvFormatException ex, CsvErrorKind kind, long record, int? field)
    {
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(record, ex.RecordNumber);
        Assert.Equal(field, ex.FieldNumber);
        Assert.DoesNotContain(Secret, ex.ToString());
        Assert.Null(ex.InnerException);
    }
}
