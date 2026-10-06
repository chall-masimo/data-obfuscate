using System.Text;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class CsvProfilerTests
{
    private static string BuildCsv(int rows)
    {
        var csv = new StringBuilder("Customer_ID,Customer_Name,Zip,Amount,Order_Date,Notes\r\n");
        for (int i = 0; i < rows; i++)
        {
            string notes = (i % 4) switch { 0 => "", 1 => "\"\"", 2 => "   ", _ => $"SECRET note {i}" };
            csv.Append($"C{i:D5},\"SECRET Corp {i}\",{(2000 + i % 300):D5},{i * 1.5:F2},2024-{i % 12 + 1:D2}-{i % 28 + 1:D2},{notes}\r\n");
        }
        return csv.ToString();
    }

    private static FileProfile Profile(string csv, ProfileOptions? options = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return CsvProfiler.Profile(stream, options);
    }

    [Fact]
    public void Profiles_every_column()
    {
        var profile = Profile(BuildCsv(400));

        Assert.True(profile.ReachedEndOfFile);
        Assert.Equal(400, profile.RowsProfiled);
        Assert.Equal(
            [DetectedType.Identifier, DetectedType.OrgName, DetectedType.Zip, DetectedType.Measure, DetectedType.Date, DetectedType.FreeText],
            profile.Columns.Select(c => c.Type));
        Assert.Equal(
            [MaskingStrategy.HashId, MaskingStrategy.Fake, MaskingStrategy.ZipRemap, MaskingStrategy.Perturb, MaskingStrategy.DateShift, MaskingStrategy.Redact],
            profile.Columns.Select(c => c.Suggestion.Strategy));
    }

    [Fact]
    public void Column_statistics()
    {
        var profile = Profile(BuildCsv(400));
        var id = profile.Columns[0];
        var amount = profile.Columns[3];
        var notes = profile.Columns[5];

        Assert.Equal(400, id.DistinctCount);
        Assert.False(id.DistinctIsEstimate);
        Assert.Equal(6, id.MinLength);
        Assert.Equal(6, id.MaxLength);
        Assert.Equal(5, id.SampleValues.Count);
        Assert.Equal("C00000", id.SampleValues[0]);

        Assert.True(amount.IsNumeric);
        Assert.False(amount.IsIntegerOnly);
        Assert.Equal(2, amount.MaxDecimalScale);

        Assert.True(profile.Columns[2].HasLeadingZeros);
        Assert.Equal(["yyyy-MM-dd"], profile.Columns[4].DateFormats);

        // Notes cycles: unquoted empty (null), "" (blank), whitespace (blank), value.
        Assert.Equal(100, notes.NullCount);
        Assert.Equal(200, notes.BlankCount);
        Assert.Equal(100, notes.ValueCount);
        Assert.Equal(0.25, notes.NullRate);
    }

    [Fact]
    public void Sample_cutoff_marks_counts_as_estimates()
    {
        var profile = Profile(BuildCsv(500), new ProfileOptions { SampleRows = 100 });

        Assert.Equal(100, profile.RowsProfiled);
        Assert.False(profile.ReachedEndOfFile);
        Assert.All(profile.Columns, c => Assert.True(c.DistinctIsEstimate));
        Assert.All(profile.Columns, c => Assert.Equal(100, c.RowCount));
    }

    [Fact]
    public void File_exactly_the_sample_size_reaches_the_end()
    {
        var profile = Profile(BuildCsv(100), new ProfileOptions { SampleRows = 100 });

        Assert.True(profile.ReachedEndOfFile);
        Assert.False(profile.Columns[0].DistinctIsEstimate);
    }

    [Fact]
    public void Distinct_count_above_exact_limit_is_estimate()
    {
        var profile = Profile(BuildCsv(400), new ProfileOptions { ExactDistinctLimit = 50 });

        Assert.True(profile.Columns[0].DistinctIsEstimate);
        Assert.InRange(profile.Columns[0].DistinctCount, 380, 420);
    }

    [Fact]
    public void Malformed_and_blank_rows_are_skipped_and_reported()
    {
        var profile = Profile("a,b\r\n1,2\r\n3\r\n\r\n4,5\r\n6,7,8\r\n9,10\r\n",
            new ProfileOptions { MaxMalformedRowsListed = 1 });

        Assert.Equal(3, profile.RowsProfiled);
        Assert.Equal(1, profile.BlankLines);
        Assert.Equal(2, profile.MalformedRowCount);
        Assert.Equal([3L], profile.MalformedRecordNumbers);
    }

    [Fact]
    public void Duplicate_headers_profile_under_unique_keys()
    {
        var profile = Profile("Name,Name,\r\na,b,c\r\n");

        Assert.Equal(["Name", "Name#2", "Column3"], profile.Columns.Select(c => c.Key));
        Assert.Equal(["Name", "Name", ""], profile.Columns.Select(c => c.OriginalName));
    }

    [Fact]
    public void Unclosed_quote_stops_the_profile()
    {
        Assert.Throws<CsvFormatException>(() => Profile("a,b\r\n1,\"open\r\n"));
    }

    [Fact]
    public void ToString_never_includes_sample_values()
    {
        var profile = Profile(BuildCsv(50));

        Assert.DoesNotContain("SECRET", profile.ToString());
        Assert.All(profile.Columns, c => Assert.DoesNotContain("SECRET", c.ToString()));
        Assert.Contains(profile.Columns, c => c.SampleValues.Any(v => v.Contains("SECRET")));
    }
}
