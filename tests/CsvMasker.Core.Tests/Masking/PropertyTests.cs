using System.Text;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests.Masking;

/// <summary>Randomly generated CSVs (seeded, so failures reproduce) checked against the universal invariants.</summary>
public class PropertyTests
{
    private static readonly string[] Columns = ["Id", "Name", "Zip", "Amount", "Date", "Notes", "Status", "Text", "Email"];

    private static readonly MaskingPlan Plan = new(new Dictionary<string, ColumnRule>
    {
        ["Id"] = new(MaskingStrategy.HashId),
        ["Name"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company)),   // company names can contain commas
        ["Zip"] = new(MaskingStrategy.ZipRemap),
        ["Amount"] = new(MaskingStrategy.Perturb),
        ["Date"] = new(MaskingStrategy.DateShift),
        ["Notes"] = new(MaskingStrategy.Redact),
        ["Status"] = new(MaskingStrategy.Keep),
        ["Text"] = new(MaskingStrategy.Lorem),
        ["Email"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Email)),
    });

    private static readonly HashSet<string> MappingColumns = ["Id", "Name", "Zip", "Email"];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Masking_preserves_shape(int seed)
    {
        var random = new Random(seed);
        string csv = Generate(random, rows: 400);
        byte[] input = Encode(csv, seed % 2 == 0 ? Utf8Bom : Utf8);

        using var session = new MaskingSession((byte[])TestMasker.KeyA.Clone(), null);
        using var output = new MemoryStream();
        var report = session.Run(new MemoryStream(input), output, Plan);

        var source = ReadAll(input);
        var masked = ReadAll(output.ToArray());

        Assert.False(report.HasFailures, string.Join("; ", report.Columns.SelectMany(c => c.Failures)));
        Assert.Equal(source.Count, masked.Count);
        for (int c = 0; c < Columns.Length; c++)
        {
            var mapping = new Dictionary<string, string>();
            for (int r = 0; r < source.Count; r++)
            {
                Assert.Equal(source[r].IsNull(c), masked[r].IsNull(c));
                Assert.Equal(source[r].LineEnding, masked[r].LineEnding);

                string s = source[r].Values[c], m = masked[r].Values[c];
                if (string.IsNullOrWhiteSpace(s))
                {
                    Assert.Equal(s, m);
                    continue;
                }
                if (!MappingColumns.Contains(Columns[c]))
                    continue;

                Assert.NotEqual(s, m);
                if (mapping.TryGetValue(s, out var previous))
                    Assert.Equal(previous, m);           // consistent within the file
                mapping[s] = m;
            }

            if (MappingColumns.Contains(Columns[c]))
                Assert.Equal(mapping.Count, mapping.Values.Distinct().Count()); // cardinality preserved
        }
    }

    private static List<CsvRecord> ReadAll(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new CsvReader(stream, CsvDialectDetector.Detect(stream));
        var records = new List<CsvRecord>();
        while (reader.TryRead(out var r)) records.Add(r);
        return records;
    }

    private static string Generate(Random random, int rows)
    {
        var csv = new StringBuilder(string.Join(',', Columns)).Append("\r\n");
        for (int r = 0; r < rows; r++)
        {
            string Maybe(string value) => random.Next(10) switch
            {
                0 => "",              // null
                1 => "\"\"",          // empty string
                2 => "\"  \"",        // whitespace
                _ => value,
            };

            int id = random.Next(150); // repeats exercise consistency
            var fields = new[]
            {
                Maybe($"ID-{id:D5}"),
                Maybe($"\"Customer {id}, Inc\""),
                Maybe(random.Next(5) == 0 ? $"{random.Next(10000, 99999)}-{random.Next(1000, 9999)}" : $"{random.Next(1000, 99999):D5}"),
                Maybe(random.Next(4) == 0 ? $"\"${random.Next(-5000, 5000):N2}\"" : $"{random.NextDouble() * 1000:F2}"),
                Maybe($"{random.Next(1, 13)}/{random.Next(1, 29)}/20{random.Next(10, 30)}"),
                Maybe($"note {random.Next()}"),
                Maybe(random.Next(2) == 0 ? "Open" : "Closed"),
                Maybe($"\"Some free text, line {r}\r\nwith a break\""),
                Maybe($"person{id}@corp.com"),
            };
            csv.Append(string.Join(',', fields)).Append(random.Next(3) == 0 ? "\n" : "\r\n");
        }
        return csv.ToString();
    }
}
