using System.Text;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Verification;
using CsvMasker.Core.Profiling;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests.Masking;

public class PipelineTests
{
    private static MaskingPlan Plan(params (string Key, ColumnRule Rule)[] rules) =>
        new(rules.ToDictionary(r => r.Key, r => r.Rule));

    private static (byte[] Output, VerificationReport Report) Run(byte[] input, MaskingPlan plan, MaskingOptions? options = null)
    {
        using var session = new MaskingSession((byte[])TestMasker.KeyA.Clone(), options);
        using var source = new MemoryStream(input, writable: false);
        using var output = new MemoryStream();
        var report = session.Run(source, output, plan);
        return (output.ToArray(), report);
    }

    [Fact]
    public void Keep_everything_is_byte_identical()
    {
        byte[] input = Encode("Id\tName\tNote\r\n1\t\"Zoë\"\t\"multi\r\nline\"\n2\t\t\"\"\r\n\r\n3\tJosé\tx", Utf16LeBom);
        var plan = Plan(("Id", new(MaskingStrategy.Keep)), ("Name", new(MaskingStrategy.Keep)), ("Note", new(MaskingStrategy.Keep)));

        var (output, report) = Run(input, plan);

        Assert.Equal(input, output);
        Assert.False(report.HasFailures);
        Assert.Equal(3, report.RowsRead);
        Assert.Equal(1, report.BlankLines);
    }

    [Fact]
    public void Masked_output_keeps_dialect_quoting_and_line_endings()
    {
        // City is kept so the output still has a 1252-only character to detect.
        byte[] input = Encode("Id;Company;City\r\n\"C001\";Acme;Zürich\n\"C002\";Beta;Köln\r\nC003;;\"\"", Win1252);
        var plan = Plan(
            ("Id", new(MaskingStrategy.HashId)),
            ("Company", new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company))),
            ("City", new(MaskingStrategy.Keep)));

        var (output, _) = Run(input, plan);

        using var stream = new MemoryStream(output);
        var dialect = CsvDialectDetector.Detect(stream);
        Assert.Equal(';', dialect.Delimiter);
        Assert.Equal("Windows-1252", dialect.Encoding.Name);
        using var reader = new CsvReader(stream, dialect);
        var records = new List<CsvRecord>();
        while (reader.TryRead(out var r)) records.Add(r);

        Assert.Equal(["\n", "\r\n", ""], records.Select(r => r.LineEnding));
        Assert.True(records[0].WasQuoted[0]);   // quoted source field stays quoted
        Assert.False(records[2].WasQuoted[0]);
        Assert.True(records[2].IsNull(1));      // null stays null
        Assert.True(records[2].WasQuoted[2]);   // "" stays ""
        Assert.Equal("", records[2].Values[2]);
    }

    [Fact]
    public void Shared_mapping_domain_maps_identically()
    {
        byte[] input = Encode("BillTo,ShipTo\r\nC1,C2\r\nC2,C1\r\nC3,C3\r\n", Utf8);
        var rule = new ColumnRule(MaskingStrategy.HashId, MappingDomain: "customer");
        var (output, _) = Run(input, Plan(("BillTo", rule), ("ShipTo", rule)));

        var rows = Encoding.UTF8.GetString(output).Split("\r\n")[1..^1].Select(l => l.Split(',')).ToArray();

        Assert.Equal(rows[0][0], rows[1][1]); // C1
        Assert.Equal(rows[0][1], rows[1][0]); // C2
        Assert.Equal(rows[2][0], rows[2][1]); // C3
    }

    [Fact]
    public void Malformed_rows_fail_by_default()
    {
        byte[] input = Encode("a,b\r\n1,2\r\n3\r\n4,5\r\n", Utf8);
        var plan = Plan(("a", new(MaskingStrategy.Keep)), ("b", new(MaskingStrategy.Keep)));

        var ex = Assert.Throws<CsvFormatException>(() => Run(input, plan));

        Assert.Equal(3, ex.RecordNumber);
    }

    [Fact]
    public void Malformed_rows_can_be_skipped_and_reported()
    {
        byte[] input = Encode("a,b\r\n1,2\r\n3\r\n4,5\r\n", Utf8);
        var plan = Plan(("a", new(MaskingStrategy.Keep)), ("b", new(MaskingStrategy.Keep)));

        var (output, report) = Run(input, plan, new MaskingOptions { FailOnMalformed = false });

        Assert.Equal("a,b\r\n1,2\r\n4,5\r\n", Encoding.UTF8.GetString(output));
        Assert.Equal(1, report.MalformedRowCount);
        Assert.Equal([3L], report.MalformedRecordNumbers);
    }

    [Fact]
    public void Preview_matches_the_start_of_the_run()
    {
        var csv = new StringBuilder("Id,Name,Amount,Date\r\n");
        for (int i = 0; i < 50; i++)
            csv.Append($"ID{i % 30},Person {i % 25},{i * 10.5:F2},2024-01-{i % 28 + 1:D2}\r\n");
        byte[] input = Encode(csv.ToString(), Utf8);
        var plan = Plan(
            ("Id", new(MaskingStrategy.HashId)),
            ("Name", new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFull))),
            ("Amount", new(MaskingStrategy.Perturb)),
            ("Date", new(MaskingStrategy.DateShift)));

        using var session = new MaskingSession((byte[])TestMasker.KeyA.Clone(), null);
        var preview = session.Preview(new MemoryStream(input), plan, rows: 20);
        using var output = new MemoryStream();
        session.Run(new MemoryStream(input), output, plan);
        var runRows = Encoding.UTF8.GetString(output.ToArray()).Split("\r\n")[1..21];

        Assert.Equal(20, preview.Count);
        Assert.Equal(runRows, preview.Select(p => string.Join(',', p.Masked)));
        Assert.Equal("ID0", preview[0].Original[0]);
    }

    [Fact]
    public void Plan_is_checked_before_any_output()
    {
        byte[] input = Encode("Id,Secret\r\n1,SECRET-VALUE\r\n", Utf8);
        using var session = new MaskingSession();
        using var output = new MemoryStream();

        var ex = Assert.Throws<MaskingPlanException>(() =>
            session.Run(new MemoryStream(input), output, Plan(("Id", new(MaskingStrategy.Keep)))));

        Assert.Equal(0, output.Length);
        Assert.Contains("'Secret'", ex.Message);
        Assert.DoesNotContain("SECRET-VALUE", ex.ToString());
    }

    [Fact]
    public void Mapping_limit_fails_cleanly()
    {
        var csv = new StringBuilder("Id\r\n");
        for (int i = 0; i < 100; i++) csv.Append($"SECRET{i:D4}\r\n");

        var ex = Assert.Throws<MaskingException>(() =>
            Run(Encode(csv.ToString(), Utf8), Plan(("Id", new(MaskingStrategy.HashId))), new MaskingOptions { MaxMappingEntries = 10 }));

        Assert.Equal(MaskingErrorKind.MappingLimitExceeded, ex.Kind);
        Assert.Equal("Id", ex.ColumnKey);
        Assert.DoesNotContain("SECRET", ex.ToString());
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        using var session = new MaskingSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            session.Run(new MemoryStream(Encode("a\r\n1\r\n", Utf8)), new MemoryStream(), Plan(("a", new(MaskingStrategy.Keep))), cancellationToken: cts.Token));
    }

    [Fact]
    public void Disposed_session_cannot_run()
    {
        var session = new MaskingSession();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            session.Run(new MemoryStream(Encode("a\r\n1\r\n", Utf8)), new MemoryStream(), Plan(("a", new(MaskingStrategy.Keep)))));
    }

    [Fact]
    public void Profile_suggestions_run_end_to_end_without_failures()
    {
        var csv = new StringBuilder("Customer_ID,Customer_Name,Contact_Email,Phone,Zip,State,Amount,Qty,Order_Date,Is_Active,Comments\r\n");
        for (int i = 0; i < 300; i++)
            csv.Append($"C{i % 120:D5},\"SECRET Corp {i % 120}\",user{i % 120}@secret.com,(555) {100 + i % 800}-{1000 + i},{(2000 + i % 90):D5}," +
                       $"{(i % 3 == 0 ? "CA" : "NY")},{i * 12.25:F2},{i % 9 + 1},2024-{i % 12 + 1:D2}-{i % 28 + 1:D2},{(i % 2 == 0 ? "Y" : "N")}," +
                       $"\"SECRET note {i}, about the long running delivery issue that was escalated twice\"\r\n");
        byte[] input = Encode(csv.ToString(), Utf8);
        var profile = CsvProfiler.Profile(new MemoryStream(input));

        var (output, report) = Run(input, MaskingPlan.FromSuggestions(profile));

        Assert.False(report.HasFailures, string.Join("; ", report.Columns.SelectMany(c => c.Failures)));
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(output));
        Assert.DoesNotContain("@secret.com", Encoding.UTF8.GetString(output));
        Assert.DoesNotContain("SECRET", report.ToString());
        Assert.All(report.Columns, c => Assert.DoesNotContain("SECRET", c.ToString()));
        Assert.All(report.Columns.Where(c => c.IsMapping), c => Assert.Equal(c.SourceDistinct, c.OutputDistinct));
    }
}
