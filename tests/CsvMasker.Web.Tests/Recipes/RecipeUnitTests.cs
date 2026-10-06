using CsvMasker.Core.Masking;
using CsvMasker.Web.Recipes;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsvMasker.Web.Tests.Recipes;

public class RecipeUnitTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CsvMaskerRecipeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Signature_normalizes_case_and_whitespace_but_not_order()
    {
        string a = HeaderSignature.Compute(["Customer ID", "Amount"]);

        Assert.Equal(a, HeaderSignature.Compute(["  customer   id ", "AMOUNT"]));
        Assert.NotEqual(a, HeaderSignature.Compute(["Amount", "Customer ID"]));
        Assert.NotEqual(a, HeaderSignature.Compute(["Customer ID", "Amount", "Amount"]));
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Exact_signature_wins_over_better_partial()
    {
        string[] file = ["Id", "Name", "Amount"];
        var exact = Recipe("exact", file, updated: 1);
        var superset = Recipe("superset", ["Id", "Name", "Amount", "Extra"], updated: 9);

        var best = RecipeMatcher.Best(file, [superset, exact]);

        Assert.Equal("exact", best!.Recipe.Name);
        Assert.Equal(RecipeMatchKind.Exact, best.Kind);
    }

    [Fact]
    public void Partial_match_needs_half_the_columns()
    {
        string[] file = ["Id", "Name", "Amount", "Region"];

        Assert.Equal(2, RecipeMatcher.Best(file, [Recipe("half", ["id", "NAME", "Other"])])!.Matched);
        Assert.Null(RecipeMatcher.Best(file, [Recipe("quarter", ["Id", "Other", "Another"])]));
    }

    [Fact]
    public void Ties_go_to_the_most_recently_updated()
    {
        string[] file = ["Id", "Name", "Amount"];

        var best = RecipeMatcher.Best(file, [Recipe("old", ["Id", "Name"], updated: 1), Recipe("new", ["Id", "Name"], updated: 5)]);

        Assert.Equal("new", best!.Recipe.Name);
    }

    [Fact]
    public void Duplicate_names_line_up_by_occurrence()
    {
        var recipe = Recipe("dupes", ["Name", "Name"]);
        recipe.Columns[0].Strategy = MaskingStrategy.Keep;
        recipe.Columns[1].Strategy = MaskingStrategy.Redact;

        var aligned = RecipeMatcher.Align(["Name", "Id", "Name", "Name"], recipe);

        Assert.Equal(MaskingStrategy.Keep, aligned[0].Strategy);
        Assert.Equal(MaskingStrategy.Redact, aligned[2].Strategy);
        Assert.False(aligned.ContainsKey(3)); // third "Name" has no counterpart
    }

    public static TheoryData<string> Rules => new(RuleCases.Keys);

    private static readonly Dictionary<string, ColumnRule> RuleCases = new()
    {
        ["keep"] = new(MaskingStrategy.Keep),
        ["hashid"] = new(MaskingStrategy.HashId, new HashIdOptions("T-")),
        ["hashid-no-prefix"] = new(MaskingStrategy.HashId, new HashIdOptions()),
        ["fake"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Hospital)),
        ["zip"] = new(MaskingStrategy.ZipRemap),
        ["perturb"] = new(MaskingStrategy.Perturb, new PerturbOptions(0.2, PerturbMode.Global, IsCount: true)),
        ["dateshift"] = new(MaskingStrategy.DateShift, new DateShiftOptions(45, DateShiftMode.Global, KeepWeekday: true)),
        ["redact"] = new(MaskingStrategy.Redact, new RedactOptions("***")),
        ["lorem"] = new(MaskingStrategy.Lorem),
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rules_round_trip_through_recipe_columns(string name)
    {
        var rule = RuleCases[name];

        var back = RuleMapper.ToRule(RuleMapper.ToRecipeColumn("Col", rule));

        Assert.Equal(rule.Strategy, back.Strategy);
        Assert.Equal(rule.Options ?? DefaultOptions(rule.Strategy), back.Options);
    }

    [Fact]
    public void Store_round_trips_and_checks_owner()
    {
        var store = Store();
        var created = store.Create("  Monthly extract  ", @"DOMAIN\alice", "sig", [new RecipeColumn { Name = "Id", Strategy = MaskingStrategy.HashId }], [], skipMalformed: true);

        var loaded = store.Get(created.Id)!;
        Assert.Equal("Monthly extract", loaded.Name);
        Assert.True(loaded.SkipMalformed);
        Assert.Single(store.List());

        Assert.Equal(RecipeChange.NotOwner, store.Update(created.Id, @"DOMAIN\bob", "sig2", [], [], false));
        Assert.Equal(RecipeChange.NotOwner, store.Delete(created.Id, @"DOMAIN\bob"));
        Assert.Equal(RecipeChange.Done, store.Update(created.Id, @"domain\ALICE", "sig2", [], [], false)); // owner match is case-insensitive
        Assert.Equal("sig2", store.Get(created.Id)!.HeaderSignature);
        Assert.Equal(RecipeChange.Done, store.Delete(created.Id, @"DOMAIN\alice"));
        Assert.Equal(RecipeChange.NotFound, store.Delete(created.Id, @"DOMAIN\alice"));
        Assert.Empty(Directory.GetFiles(_folder)); // no temp files left behind
    }

    [Fact]
    public void Corrupt_or_future_files_are_skipped()
    {
        var store = Store();
        store.Create("good", "u", "sig", [], [], false);
        File.WriteAllText(Path.Combine(_folder, $"{Guid.NewGuid()}.json"), "{ not json");
        File.WriteAllText(Path.Combine(_folder, $"{Guid.NewGuid()}.json"), $$"""{ "schemaVersion": 99, "id": "{{Guid.NewGuid()}}" }""");

        Assert.Equal(["good"], store.List().Select(r => r.Name));
    }

    private RecipeStore Store() => new(_folder, TimeProvider.System, NullLogger<RecipeStore>.Instance);

    private static Recipe Recipe(string name, string[] columns, int updated = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Owner = "u",
        Updated = DateTimeOffset.UnixEpoch.AddDays(updated),
        HeaderSignature = HeaderSignature.Compute(columns),
        Columns = columns.Select(c => new RecipeColumn { Name = c, Strategy = MaskingStrategy.Keep }).ToList(),
    };

    private static StrategyOptions? DefaultOptions(MaskingStrategy strategy) => strategy switch
    {
        MaskingStrategy.HashId => new HashIdOptions(),
        _ => null,
    };
}
