using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class CsvReaderTests
{
    [Fact]
    public void Values_are_unescaped_strings_with_quote_flags()
    {
        var (_, records) = ReadAll("a,b,c,d\r\n007,\"x,\"\"y\"\"\r\nz\",,\"\"\r\n");

        var record = Assert.Single(records);
        Assert.Equal(["007", "x,\"y\"\r\nz", "", ""], record.Values);
        Assert.Equal([false, true, false, true], record.WasQuoted);
        Assert.Equal("\r\n", record.LineEnding);
        Assert.Equal(2, record.RecordNumber);
    }

    [Fact]
    public void Unquoted_empty_is_null_and_quoted_empty_is_not()
    {
        var (_, records) = ReadAll("a,b\r\n,\"\"\r\n");

        Assert.True(records[0].IsNull(0));
        Assert.False(records[0].IsNull(1));
    }

    [Fact]
    public void Blank_lines_are_flagged_and_skip_field_count_check()
    {
        var (_, records) = ReadAll("a,b\r\n1,2\r\n\r\n3,4");

        Assert.Equal([false, true, false], records.Select(r => r.IsBlankLine));
        Assert.Equal([2L, 3L, 4L], records.Select(r => r.RecordNumber));
        Assert.Equal("", records[^1].LineEnding);
    }

    [Fact]
    public void Empty_line_in_single_column_file_is_a_null_value()
    {
        var (_, records) = ReadAll("Name\r\nA\r\n\r\nB\r\n");

        Assert.Equal(3, records.Count);
        Assert.False(records[1].IsBlankLine);
        Assert.True(records[1].IsNull(0));
    }

    [Fact]
    public void Field_count_mismatch_leaves_reader_usable()
    {
        using var stream = new MemoryStream(Encode("a,b\r\n1\r\n2,3\r\n", Utf8));
        using var reader = new Csv.CsvReader(stream, Dialect(Utf8));

        Assert.Throws<Csv.CsvFormatException>(() => reader.TryRead(out _));
        Assert.True(reader.TryRead(out var next));
        Assert.Equal(["2", "3"], next.Values);
    }
}
