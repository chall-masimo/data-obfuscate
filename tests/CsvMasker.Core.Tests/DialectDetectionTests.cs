using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class DialectDetectionTests
{
    [Theory]
    [InlineData("Id,Name,Amount\r\n1,a,2\r\n", ',')]
    [InlineData("Id\tName\tAmount\r\n1\ta\t2\r\n", '\t')]
    [InlineData("Id|Name|Amount\r\n1|a|2\r\n", '|')]
    [InlineData("Id;Name;Amount\r\n1;a;2\r\n", ';')]
    [InlineData("Id;Price\r\n1;10,50\r\n", ';')]            // decimal commas in data don't matter
    [InlineData("\"a,b\";c;d\r\n1;2;3\r\n", ';')]           // delimiters inside quotes don't count
    [InlineData("OnlyColumn\r\nx\r\n", ',')]                // single column defaults to comma
    public void Detects_delimiter_from_header(string text, char expected)
    {
        Assert.Equal(expected, Detect(text).Delimiter);
    }

    [Theory]
    [InlineData("a,b;c\r\n1,2;3;4\r\n1,2;3;4\r\n", ',')]   // comma gives 2 fields per row, semicolon 3
    [InlineData("a,b;c\r\n1;2,3,4\r\n1;2,3,4\r\n", ';')]   // semicolon gives 2 fields per row, comma 3
    [InlineData("a,b;c\r\n", ',')]                          // no data: preference order wins
    public void Breaks_header_ties_using_data_rows(string text, char expected)
    {
        Assert.Equal(expected, Detect(text).Delimiter);
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n", "\r\n")]
    [InlineData("a,b\n1,2\r\n", "\n")]
    [InlineData("a,b\r1,2\r", "\r")]
    [InlineData("a,b", "\r\n")]                             // no terminator: default
    [InlineData("\"a\r\nb\",c\n1,2\n", "\n")]               // line break inside quoted header name
    public void Detects_header_line_ending(string text, string expected)
    {
        Assert.Equal(expected, Detect(text).LineEnding);
    }

    [Fact]
    public void Detects_delimiter_in_utf16()
    {
        using var stream = new MemoryStream(Encode("a\tb\r\n1\t2\r\n", Utf16BeBom));

        var dialect = CsvDialectDetector.Detect(stream);

        Assert.Equal('\t', dialect.Delimiter);
        Assert.Equal("UTF-16 BE (BOM)", dialect.Encoding.Name);
        Assert.Equal(0, stream.Position);
    }

    private static CsvDialect Detect(string text)
    {
        using var stream = new MemoryStream(Encode(text, Utf8), writable: false);
        return CsvDialectDetector.Detect(stream);
    }
}
