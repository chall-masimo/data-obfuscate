using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class HeaderTests
{
    [Theory]
    [InlineData(new[] { "Id", "Name" }, new[] { "Id", "Name" })]
    [InlineData(new[] { "Name", "Name", "Id", "Name" }, new[] { "Name", "Name#2", "Id", "Name#3" })]
    [InlineData(new[] { "Id", "", "Name", " " }, new[] { "Id", "Column2", "Name", "Column4" })]
    [InlineData(new[] { "Name", "Name", "Name#2" }, new[] { "Name", "Name#3", "Name#2" })]   // real Name#2 keeps its name
    [InlineData(new[] { "Column2", "" }, new[] { "Column2", "Column2#2" })]                  // generated blank key avoids a real name
    [InlineData(new[] { "", "" }, new[] { "Column1", "Column2" })]
    [InlineData(new[] { "id", "ID" }, new[] { "id", "ID" })]                                  // ordinal comparison
    public void Builds_unique_keys(string[] names, string[] expected)
    {
        Assert.Equal(expected, CsvHeader.BuildKeys(names));
    }

    [Fact]
    public void Reader_keeps_original_names_and_exposes_unique_keys()
    {
        var (header, _) = ReadAll("Name,Name,,\"Last, First\"\r\n1,2,3,4\r\n");

        Assert.Equal(["Name", "Name", "", "Last, First"], header.OriginalNames);
        Assert.Equal(["Name", "Name#2", "Column3", "Last, First"], header.Keys);
        Assert.Equal(1, header.IndexOf("Name#2"));
        Assert.Equal(-1, header.IndexOf("Missing"));
        Assert.Equal(1, header.Record.RecordNumber);
    }
}
