using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Masking.Verification;

namespace CsvMasker.Core.Tests.Masking;

public class HashIdAndZipTests
{
    [Theory]
    [InlineData("00123", @"^0\d{4}$")]                       // zero-padded stays zero-padded
    [InlineData("AB-1234-x", @"^[A-Z]{2}-[1-9]\d{3}-[a-z]$")]
    [InlineData("C00042", @"^[A-Z]0\d{4}$")]
    [InlineData("1000", @"^[1-9]\d{3}$")]                   // no new leading zero
    [InlineData("A7", @"^[A-Z]\d$")]
    [InlineData("ab.cd@ef", @"^[a-z]{2}\.[a-z]{2}@[a-z]{2}$")]
    public void HashId_keeps_format(string source, string pattern)
    {
        string masked = new TestMasker(MaskingStrategy.HashId).Mask(source);

        Assert.Matches(pattern, masked);
        Assert.NotEqual(source, masked);
    }

    [Fact]
    public void HashId_prefix()
    {
        Assert.Matches(@"^T-\d{4}$", new TestMasker(MaskingStrategy.HashId, new HashIdOptions("T-")).Mask("1234"));
    }

    [Fact]
    public void HashId_is_consistent_within_a_key_and_differs_across_keys()
    {
        var a = new TestMasker(MaskingStrategy.HashId);
        string first = a.Mask("CUST-0042");

        Assert.Equal(first, a.Mask("CUST-0042"));
        Assert.Equal(first, new TestMasker(MaskingStrategy.HashId).Mask("CUST-0042"));
        Assert.NotEqual(first, new TestMasker(MaskingStrategy.HashId, key: TestMasker.KeyB).Mask("CUST-0042"));
    }

    [Fact]
    public void HashId_keeps_cardinality_on_dense_ids()
    {
        var masker = new TestMasker(MaskingStrategy.HashId);
        // 1,000 zero-padded IDs fill the "0ddd" output space completely, so the last ones are only
        // found by stepping through the space; 1000–4999 share the "[1-9]ddd" space.
        var sources = Enumerable.Range(0, 5_000).Select(i => i.ToString("D4")).ToArray();

        var outputs = sources.Select(s => masker.Mask(s)).ToArray();

        Assert.Equal(sources.Length, outputs.Distinct().Count());
        Assert.All(sources.Zip(outputs), p => Assert.NotEqual(p.First, p.Second));
        Assert.Empty(masker.Verification.Failures);
    }

    [Fact]
    public void Mapping_fails_cleanly_when_no_unique_value_exists()
    {
        // A strategy that can only ever produce one value: the second distinct source has nowhere to go.
        var verifier = new ColumnVerifier("Col", MaskingStrategy.HashId, isMapping: true);
        var masker = new ColumnMasker("Col", new ConstantStrategy(), "Col", new MappingStore(100), verifier);
        var row = new CsvRecord(["x"], [false], "\r\n", 2);
        masker.Mask("SECRET-1", false, row);

        var ex = Assert.Throws<MaskingException>(() => masker.Mask("SECRET-2", false, row));

        Assert.Equal(MaskingErrorKind.UniqueValueExhausted, ex.Kind);
        Assert.Equal("Col", ex.ColumnKey);
        Assert.DoesNotContain("SECRET", ex.ToString());
    }

    [Fact]
    public void HashId_fills_a_completely_full_space()
    {
        // 90 two-digit IDs into the 90 "[1-9]d" outputs: only stepping through the space finds the last ones.
        var masker = new TestMasker(MaskingStrategy.HashId);
        var sources = Enumerable.Range(10, 90).Select(i => i.ToString()).ToArray();

        var outputs = sources.Select(s => masker.Mask(s)).ToArray();

        Assert.Equal(90, outputs.Distinct().Count());
        Assert.All(sources.Zip(outputs), p => Assert.NotEqual(p.First, p.Second));
    }

    private sealed class ConstantStrategy : IMaskingStrategy
    {
        public bool IsMapping => true;
        public string? Mask(in MaskInput input, int attempt) => "SAME";
    }

    [Fact]
    public void HashId_value_with_nothing_to_mask_is_redacted()
    {
        var masker = new TestMasker(MaskingStrategy.HashId);

        Assert.Equal(RedactOptions.DefaultText, masker.Mask("--"));
        Assert.Equal(1, masker.Verification.RedactedUnmaskableCount);
    }

    [Theory]
    [InlineData("02134", @"^021\d\d$")]
    [InlineData("90210", @"^902\d\d$")]
    [InlineData("02134-1234", @"^021\d\d-\d{4}$")]
    [InlineData("2134", @"^21\d\d$")]      // leading zero stripped upstream: stays 4 digits
    [InlineData("213", @"^2\d\d$")]
    public void ZipRemap_keeps_prefix_and_shape(string source, string pattern)
    {
        string masked = new TestMasker(MaskingStrategy.ZipRemap).Mask(source);

        Assert.Matches(pattern, masked);
        Assert.NotEqual(source, masked);
    }

    [Fact]
    public void ZipRemap_fallback_is_flagged_and_bad_values_redacted()
    {
        var masker = new TestMasker(MaskingStrategy.ZipRemap);
        masker.Mask("02134");

        Assert.Equal(RedactOptions.DefaultText, masker.Mask("N/A"));
        Assert.Contains(masker.Verification.Warnings, w => w.Contains("may not geocode"));
    }

    [Fact]
    public void ZipRemap_uses_a_reference_list_when_given()
    {
        var reference = new ListZipReference(["02108", "02109", "02110", "02111"]);
        var masker = new TestMasker(new ColumnRule(MaskingStrategy.ZipRemap), options: new MaskingOptions { ZipReference = reference });

        string masked = masker.Mask("02134");

        Assert.Contains(masked, reference.Zips);
        Assert.DoesNotContain(masker.Verification.Warnings, w => w.Contains("geocode"));
    }

    private sealed class ListZipReference(string[] zips) : IZipReference
    {
        public string[] Zips => zips;
        public bool IsRealList => true;
        public IReadOnlyList<string>? ZipsWithPrefix(string prefix) => zips.Where(z => z.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
