using System.Text;
using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class CsvWriterTests
{
    [Fact]
    public void New_values_are_quoted_by_strict_rfc_rules()
    {
        string output = Write(Dialect(Utf8), w =>
            w.WriteRecord(["plain", "a,b", "x\"y", "line\nbreak", ""]));

        Assert.Equal("plain,\"a,b\",\"x\"\"y\",\"line\nbreak\",\r\n", output);
    }

    [Fact]
    public void Source_unquoted_field_is_quoted_when_new_value_needs_it()
    {
        string output = Write(Dialect(Utf8, ';'), w =>
            w.WriteRecord(["a;b", "\"lead", "mid\"quote", "ok"], [false, false, false, false], "\n"));

        Assert.Equal("\"a;b\";\"\"\"lead\";mid\"quote;ok\n", output);
    }

    [Fact]
    public void Writes_bom_even_with_no_records()
    {
        using var stream = new MemoryStream();
        new CsvWriter(stream, Dialect(Utf8Bom)).Dispose();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, stream.ToArray());
    }

    [Fact]
    public void Unencodable_character_reports_record_without_content()
    {
        using var stream = new MemoryStream();
        using var writer = new CsvWriter(stream, Dialect(Win1252));
        writer.WriteRecord(["Name"]);

        var ex = Assert.Throws<CsvFormatException>(() => writer.WriteRecord(["SECRET-\U0001F600"]));

        Assert.Equal(CsvErrorKind.UnencodableOutput, ex.Kind);
        Assert.Equal(2, ex.RecordNumber);
        Assert.DoesNotContain("SECRET", ex.ToString());
    }

    [Fact]
    public void Record_after_final_record_without_line_ending_is_rejected()
    {
        using var writer = new CsvWriter(new MemoryStream(), Dialect(Utf8));
        writer.WriteRecord(["a"], lineEnding: "");

        Assert.Throws<InvalidOperationException>(() => writer.WriteRecord(["b"]));
    }

    private static string Write(CsvDialect dialect, Action<CsvWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new CsvWriter(stream, dialect, leaveOpen: true))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
