using System.Globalization;
using System.Text;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Verification;
using CsvMasker.Core.Profiling;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests.Masking;

/// <summary>Entity groups: columns seeded from an anchor so one source entity gets one coherent identity.</summary>
public class EntityGroupTests
{
    private static (List<CsvRecord> Source, List<CsvRecord> Masked, VerificationReport Report) Run(string csv, MaskingPlan plan)
    {
        byte[] input = Encode(csv, Utf8);
        using var session = new MaskingSession((byte[])TestMasker.KeyA.Clone(), null);
        using var output = new MemoryStream();
        var report = session.Run(new MemoryStream(input), output, plan);
        return (ReadAll(input), ReadAll(output.ToArray()), report);
    }

    private static List<CsvRecord> ReadAll(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new CsvReader(stream, CsvDialectDetector.Detect(stream));
        var records = new List<CsvRecord>();
        while (reader.TryRead(out var r)) records.Add(r);
        return records;
    }

    private static MaskingPlan Plan(Dictionary<string, ColumnRule> rules, params EntityGroup[] groups) => new(rules, groups);

    [Fact]
    public void One_entity_gets_one_coherent_identity()
    {
        var csv = new StringBuilder("Customer_ID,Name,First,Last,Email\r\n");
        for (int i = 0; i < 400; i++)
            csv.Append($"C{i % 150},Real Person {i % 150},Real{i % 150},Person{i % 150},real{i % 150}@corp.com\r\n");
        var plan = Plan(new()
        {
            ["Customer_ID"] = new(MaskingStrategy.HashId),
            ["Name"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFull)),
            ["First"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFirst)),
            ["Last"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonLast)),
            ["Email"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Email)),
        }, new EntityGroup("Customer_ID", ["Name", "First", "Last", "Email"]));

        var (source, masked, report) = Run(csv.ToString(), plan);

        Assert.False(report.HasFailures, string.Join("; ", report.Columns.SelectMany(c => c.Failures)));
        for (int r = 0; r < masked.Count; r++)
        {
            var row = masked[r].Values;
            Assert.Equal($"{row[2]} {row[3]}", row[1]); // full name = first + last
            string first = new string(row[2].Where(char.IsLetter).ToArray()).ToLowerInvariant();
            Assert.StartsWith(first, new string(row[4].TakeWhile(c => c != '@').Where(char.IsLetter).ToArray()));
        }

        // Every row of one customer is masked the same way.
        foreach (var customer in source.Select((s, i) => (Id: s.Values[0], Row: masked[i].Values)).GroupBy(x => x.Id))
            Assert.Single(customer.Select(x => string.Join("|", x.Row)).Distinct());
    }

    [Fact]
    public void Same_name_under_two_entities_gets_two_fakes_and_variants_stay_distinct()
    {
        const string csv = "Id,Company\r\nA,Acme\r\nB,Acme\r\nA,Acme\r\nA,ACME Inc\r\n,Acme\r\n,Acme\r\n";
        var plan = Plan(new()
        {
            ["Id"] = new(MaskingStrategy.Keep),
            ["Company"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company)),
        }, new EntityGroup("Id", ["Company"]));

        var (_, masked, report) = Run(csv, plan);
        var names = masked.Select(m => m.Values[1]).ToArray();

        Assert.Equal(names[0], names[2]);    // A/Acme twice: same output
        Assert.NotEqual(names[0], names[1]); // B/Acme: another entity, another fake
        Assert.NotEqual(names[0], names[3]); // A/ACME Inc: a variant within A stays distinct
        Assert.Equal(names[4], names[5]);    // blank anchor: falls back to value-based mapping
        Assert.DoesNotContain("Acme", names);
        var company = report.Columns[1];
        Assert.True(company.PerEntity);
        Assert.Equal(company.SourceDistinct, company.OutputDistinct);
        Assert.Empty(company.Failures);
    }

    [Fact]
    public void Per_entity_perturb_moves_one_entitys_amounts_together()
    {
        var csv = new StringBuilder("Id,Amount\r\n");
        for (int i = 0; i < 60; i++)
            csv.Append($"E{i % 6},{1000 + i * 10}.0000\r\n");
        var plan = Plan(new()
        {
            ["Id"] = new(MaskingStrategy.Keep),
            ["Amount"] = new(MaskingStrategy.Perturb, new PerturbOptions(Mode: PerturbMode.PerEntity)),
        }, new EntityGroup("Id", ["Amount"]));

        var (source, masked, _) = Run(csv.ToString(), plan);
        var factors = source.Zip(masked, (s, m) => (Id: s.Values[0],
            Factor: decimal.Parse(m.Values[1], CultureInfo.InvariantCulture) / decimal.Parse(s.Values[1], CultureInfo.InvariantCulture)));

        var perEntity = factors.GroupBy(f => f.Id).Select(g => g.Select(f => Math.Round(f.Factor, 3)).Distinct().ToList()).ToList();
        Assert.All(perEntity, distinctFactors => Assert.Single(distinctFactors));
        Assert.True(perEntity.Select(f => f[0]).Distinct().Count() > 1); // different entities, different factors
    }

    [Fact]
    public void Per_entity_date_shift_moves_one_entitys_dates_together()
    {
        var csv = new StringBuilder("Id,Date\r\n");
        for (int i = 0; i < 60; i++)
            csv.Append($"E{i % 6},{new DateTime(2024, 1, 1).AddDays(i):yyyy-MM-dd}\r\n");
        var plan = Plan(new()
        {
            ["Id"] = new(MaskingStrategy.Keep),
            ["Date"] = new(MaskingStrategy.DateShift, new DateShiftOptions(MaxDays: 60, Mode: DateShiftMode.PerEntity, KeepWeekday: true)),
        }, new EntityGroup("Id", ["Date"]));

        var (source, masked, _) = Run(csv.ToString(), plan);
        var offsets = source.Zip(masked, (s, m) => (Id: s.Values[0],
            Days: (DateTime.ParseExact(m.Values[1], "yyyy-MM-dd", CultureInfo.InvariantCulture) - DateTime.ParseExact(s.Values[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)).Days));

        var perEntity = offsets.GroupBy(o => o.Id).Select(g => g.Select(o => o.Days).Distinct().Single()).ToList();
        Assert.All(perEntity, days => Assert.True(days != 0 && days % 7 == 0));
        Assert.True(perEntity.Distinct().Count() > 1);
    }

    [Fact]
    public void Linked_hashid_and_zip_keep_their_own_formats()
    {
        const string csv = "Id,Account,Zip\r\nA,AC-0001,02134\r\nA,AC-0001,02134\r\nB,AC-0002,90210\r\n";
        var plan = Plan(new()
        {
            ["Id"] = new(MaskingStrategy.Keep),
            ["Account"] = new(MaskingStrategy.HashId),
            ["Zip"] = new(MaskingStrategy.ZipRemap),
        }, new EntityGroup("Id", ["Account", "Zip"]));

        var (_, masked, report) = Run(csv, plan);

        Assert.All(masked, m => Assert.Matches(@"^[A-Z]{2}-0\d{3}$", m.Values[1]));
        Assert.Matches(@"^021\d\d$", masked[0].Values[2]);
        Assert.Matches(@"^902\d\d$", masked[2].Values[2]);
        Assert.Equal(masked[0].Values[1], masked[1].Values[1]);
        Assert.False(report.HasFailures);
    }

    [Theory]
    [InlineData("unknown-anchor")]
    [InlineData("unknown-member")]
    [InlineData("self-link")]
    [InlineData("two-groups")]
    [InlineData("anchor-linked")]
    [InlineData("empty-group")]
    [InlineData("linked-with-domain")]
    [InlineData("per-entity-unlinked")]
    public void Invalid_groups_are_rejected(string scenario)
    {
        var rules = new Dictionary<string, ColumnRule>
        {
            ["A"] = new(MaskingStrategy.Keep),
            ["B"] = new(MaskingStrategy.Keep),
            ["C"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company), scenario == "linked-with-domain" ? "shared" : null),
            ["D"] = new(MaskingStrategy.Perturb, new PerturbOptions(Mode: scenario == "per-entity-unlinked" ? PerturbMode.PerEntity : PerturbMode.PerRow)),
        };
        EntityGroup[] groups = scenario switch
        {
            "unknown-anchor" => [new("X", ["C"])],
            "unknown-member" => [new("A", ["X"])],
            "self-link" => [new("A", ["A"])],
            "two-groups" => [new("A", ["C"]), new("B", ["C"])],
            "anchor-linked" => [new("A", ["B"]), new("B", ["C"])],
            "empty-group" => [new("A", [])],
            _ => [new("A", ["C"])],
        };
        var header = new CsvHeader(new CsvRecord(["A", "B", "C", "D"], [false, false, false, false], "\r\n", 1));

        Assert.Throws<MaskingPlanException>(() => new MaskingPlan(rules, groups).Validate(header));
    }

    [Fact]
    public void Groups_are_suggested_from_column_names()
    {
        var csv = new StringBuilder("Customer_ID,Customer_Name,Customer_Email,Customer_City,Region,Account_Owner_ID,Account_Owner_Name\r\n");
        for (int i = 0; i < 200; i++)
            csv.Append($"C{i:D5},Acme {i},user{i}@corp.com,City {i % 20},R{i % 4},U{i % 150:D4},Person {i % 150}\r\n");
        var profile = CsvProfiler.Profile(new MemoryStream(Encode(csv.ToString(), Utf8)));

        var groups = MaskingPlan.FromSuggestions(profile).EntityGroups;

        var customer = Assert.Single(groups, g => g.Anchor == "Customer_ID");
        Assert.Equal(["Customer_Name", "Customer_Email"], customer.Members);
        var owner = Assert.Single(groups, g => g.Anchor == "Account_Owner_ID");
        Assert.Equal(["Account_Owner_Name"], owner.Members);
    }
}
