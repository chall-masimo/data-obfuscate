using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;

namespace CsvMasker.Core.Tests.Masking;

public class InvariantAndPlanTests
{
    public static TheoryData<string> AllRules => new(Rules.Keys);

    private static readonly Dictionary<string, ColumnRule> Rules = new()
    {
        ["Keep"] = new(MaskingStrategy.Keep),
        ["HashId"] = new(MaskingStrategy.HashId),
        ["Fake"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFull)),
        ["FakePhone"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Phone)),
        ["ZipRemap"] = new(MaskingStrategy.ZipRemap),
        ["Perturb"] = new(MaskingStrategy.Perturb),
        ["DateShift"] = new(MaskingStrategy.DateShift),
        ["Redact"] = new(MaskingStrategy.Redact),
        ["Lorem"] = new(MaskingStrategy.Lorem),
    };

    [Theory]
    [MemberData(nameof(AllRules))]
    public void Null_empty_and_whitespace_pass_through(string rule)
    {
        var masker = new TestMasker(Rules[rule]);

        Assert.Equal("", masker.Mask("", isNull: true));
        Assert.Equal("", masker.Mask(""));
        Assert.Equal("   ", masker.Mask("   "));
        Assert.Equal("\t", masker.Mask("\t"));
    }

    [Fact]
    public void Redact_and_lorem()
    {
        Assert.Equal("[REDACTED]", new TestMasker(MaskingStrategy.Redact).Mask("secret"));
        Assert.Equal("***", new TestMasker(MaskingStrategy.Redact, new RedactOptions("***")).Mask("secret"));

        var lorem = new TestMasker(MaskingStrategy.Lorem);
        string source = "The customer asked about a refund for order 12345.";
        string masked = lorem.Mask(source);
        Assert.Equal(source.Length, masked.Length);
        Assert.NotEqual(source, masked);
        Assert.Equal(masked, lorem.Mask(source));
    }

    [Fact]
    public void Plan_must_cover_every_column()
    {
        var plan = new MaskingPlan(new Dictionary<string, ColumnRule>
        {
            ["Id"] = new(MaskingStrategy.HashId),
            ["Extra"] = new(MaskingStrategy.Keep),
        });

        var ex = Assert.Throws<MaskingPlanException>(() => PlanValidator.Validate(plan, Header("Id", "Name")));

        Assert.Contains(ex.Problems, p => p.Contains("'Name' has no strategy"));
        Assert.Contains(ex.Problems, p => p.Contains("'Extra'"));
    }

    [Theory]
    [InlineData("fake-without-kind")]
    [InlineData("wrong-options")]
    [InlineData("bad-percent")]
    [InlineData("per-entity")]
    [InlineData("weekday-too-short")]
    [InlineData("shared-domain-mismatch")]
    public void Plan_rejects_bad_options(string scenario)
    {
        var rules = scenario switch
        {
            "fake-without-kind" => new Dictionary<string, ColumnRule> { ["A"] = new(MaskingStrategy.Fake) },
            "wrong-options" => new() { ["A"] = new(MaskingStrategy.Keep, new RedactOptions()) },
            "bad-percent" => new() { ["A"] = new(MaskingStrategy.Perturb, new PerturbOptions(Percent: 1.5)) },
            "per-entity" => new() { ["A"] = new(MaskingStrategy.DateShift, new DateShiftOptions(Mode: DateShiftMode.PerEntity)) },
            "weekday-too-short" => new() { ["A"] = new(MaskingStrategy.DateShift, new DateShiftOptions(MaxDays: 5, KeepWeekday: true)) },
            _ => new()
            {
                ["A"] = new(MaskingStrategy.HashId, MappingDomain: "customer"),
                ["B"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company), "customer"),
            },
        };
        var names = rules.Keys.ToArray();

        Assert.Throws<MaskingPlanException>(() => PlanValidator.Validate(new MaskingPlan(rules), Header(names)));
    }

    [Fact]
    public void Valid_plan_passes()
    {
        var plan = new MaskingPlan(Rules.ToDictionary(r => r.Key, r => r.Value));

        PlanValidator.Validate(plan, Header(Rules.Keys.ToArray()));
    }

    private static CsvHeader Header(params string[] names) =>
        new(new CsvRecord(names, names.Select(_ => false).ToArray(), "\r\n", 1));
}
