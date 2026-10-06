using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Strategies;

namespace CsvMasker.Core.Tests.Masking;

public class FakeTests
{
    private static TestMasker Masker(FakeKind kind, bool preserveCase = true, byte[]? key = null) =>
        new(MaskingStrategy.Fake, new FakeOptions(kind, preserveCase), key);

    [Theory]
    [InlineData(FakeKind.PersonFirst, @"^\S+$")]
    [InlineData(FakeKind.PersonLast, @"^\S.*$")]
    [InlineData(FakeKind.PersonFull, @"^\S+ \S.*$")]
    [InlineData(FakeKind.Company, @"^\S.*$")]
    [InlineData(FakeKind.Hospital, @" (Medical Center|Hospital|Health)$")]
    [InlineData(FakeKind.StreetAddress, @"^\d+ \S.*$")]
    [InlineData(FakeKind.City, @"^\S.*$")]
    [InlineData(FakeKind.Email, @"^[^@\s]+@example\.(com|net|org)$")]
    public void Generates_each_kind(FakeKind kind, string pattern)
    {
        string masked = Masker(kind, preserveCase: false).Mask("Source Value 1");

        Assert.Matches(pattern, masked);
    }

    [Fact]
    public void Emails_only_use_reserved_domains()
    {
        var masker = Masker(FakeKind.Email);

        var outputs = Enumerable.Range(0, 500).Select(i => masker.Mask($"person{i}@realcompany.com")).ToArray();

        Assert.All(outputs, e => Assert.Matches(@"@example\.(com|net|org)$", e));
        Assert.Equal(500, outputs.Distinct().Count());
    }

    [Fact]
    public void Same_value_gives_same_fake_regardless_of_order()
    {
        var first = Masker(FakeKind.PersonFull);
        string alice = first.Mask("Alice Smith");
        first.Mask("Bob Jones");

        var second = Masker(FakeKind.PersonFull);
        second.Mask("Bob Jones");
        second.Mask("Carol White");

        Assert.Equal(alice, second.Mask("Alice Smith"));
        Assert.Equal(alice, first.Mask("Alice Smith"));
    }

    [Fact]
    public void Different_keys_give_different_fakes()
    {
        var outputsA = Enumerable.Range(0, 20).Select(i => Masker(FakeKind.PersonFull).Mask($"Name {i}"));
        var outputsB = Enumerable.Range(0, 20).Select(i => Masker(FakeKind.PersonFull, key: TestMasker.KeyB).Mask($"Name {i}"));

        Assert.NotEqual(outputsA, outputsB);
    }

    [Theory]
    [InlineData("JOHN SMITH", true)]
    [InlineData("john smith", false)]
    public void Copies_all_caps_or_all_lower_case(string source, bool upper)
    {
        string masked = Masker(FakeKind.PersonFull).Mask(source);

        Assert.Equal(upper ? masked.ToUpperInvariant() : masked.ToLowerInvariant(), masked);
    }

    [Theory]
    [InlineData("(555) 123-4567", @"^\([2-9]\d\d\) [2-9]\d\d-\d{4}$")]
    [InlineData("555.123.4567", @"^[2-9]\d\d\.[2-9]\d\d\.\d{4}$")]
    [InlineData("1-555-123-4567", @"^1-[2-9]\d\d-[2-9]\d\d-\d{4}$")]
    [InlineData("5551234567 x12", @"^[2-9]\d\d[2-9]\d{6} x\d\d$")]
    public void Phone_keeps_source_format(string source, string pattern)
    {
        string masked = Masker(FakeKind.Phone).Mask(source);

        Assert.Matches(pattern, masked);
        Assert.NotEqual(source, masked);
    }

    [Fact]
    public void Exhausted_name_pool_gets_suffixes_and_stays_unique()
    {
        var masker = Masker(FakeKind.PersonFirst);
        var sources = Enumerable.Range(0, 6_000).Select(i => $"Name{i}").ToArray();

        var outputs = sources.Select(s => masker.Mask(s)).ToArray();
        var verification = masker.Verification;

        Assert.Equal(sources.Length, outputs.Distinct().Count());
        Assert.True(verification.DisambiguatedCount > 0);
        Assert.Contains(verification.Warnings, w => w.Contains("suffix"));
        Assert.Empty(verification.Failures);
    }

    [Fact]
    public void Email_suffix_goes_before_the_at_sign()
    {
        var strategy = new FakeStrategy(new SeedSource((byte[])TestMasker.KeyA.Clone()), new FakeOptions(FakeKind.Email));

        Assert.Equal("jane.doe7@example.com", strategy.Disambiguate("jane.doe@example.com", 7));
    }
}
