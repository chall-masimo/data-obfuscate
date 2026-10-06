using System.Globalization;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Strategies;

namespace CsvMasker.Core.Tests.Masking;

public class PerturbTests
{
    [Theory]
    [InlineData("1,234.50")]
    [InlineData("$1,234.50")]
    [InlineData("-$5")]
    [InlineData("$-5")]
    [InlineData("(12.00)")]
    [InlineData("($1,000)")]
    [InlineData("+3")]
    [InlineData(".75")]
    [InlineData(" 42 ")]
    [InlineData("007")]
    [InlineData("0.10")]
    [InlineData("1234567.891")]
    public void NumberFormat_round_trips(string text)
    {
        Assert.True(NumberFormat.TryParse(text, out var number));

        Assert.Equal(text, number.Format(number.Value));
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("1,23")]
    [InlineData("--5")]
    [InlineData("(-5)")]
    [InlineData("1.")]
    [InlineData("$")]
    public void NumberFormat_rejects(string text) => Assert.False(NumberFormat.TryParse(text, out _));

    [Theory]
    [InlineData("-12.50", @"^-\d+\.\d\d$")]
    [InlineData("$1,234.50", @"^\$\d{1,3}(,\d{3})*\.\d\d$")]
    [InlineData("(12.00)", @"^\(\d+\.\d\d\)$")]
    [InlineData("4500", @"^\d+$")]
    [InlineData("0.125", @"^0\.\d{3}$")]
    public void Keeps_sign_scale_and_formatting(string source, string pattern)
    {
        Assert.Matches(pattern, new TestMasker(MaskingStrategy.Perturb).Mask(source));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("$0")]
    public void Zero_stays_zero(string source)
    {
        Assert.Equal(source, new TestMasker(MaskingStrategy.Perturb).Mask(source));
    }

    [Fact]
    public void Factor_stays_within_percent()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb, new PerturbOptions(Percent: 0.10));

        for (int i = 0; i < 1_000; i++)
        {
            decimal source = 10_000 + i;
            decimal masked = decimal.Parse(masker.Mask(source.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            Assert.InRange(masked / source, 0.8999m, 1.1001m);
        }
    }

    [Fact]
    public void Counts_never_drop_below_one()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb, new PerturbOptions(Percent: 0.9, IsCount: true));

        Assert.All(Enumerable.Range(0, 500), _ => Assert.True(int.Parse(masker.Mask("1")) >= 1));
    }

    [Fact]
    public void Small_values_never_round_to_zero()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb, new PerturbOptions(Percent: 0.9));

        Assert.All(Enumerable.Range(0, 200), _ => Assert.NotEqual("0.00", masker.Mask("0.01")));
    }

    [Fact]
    public void Global_mode_uses_one_factor()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb, new PerturbOptions(Mode: PerturbMode.Global));

        decimal a = decimal.Parse(masker.Mask("1000.0000"), CultureInfo.InvariantCulture) / 1000;
        decimal b = decimal.Parse(masker.Mask("2000.0000"), CultureInfo.InvariantCulture) / 2000;
        decimal c = decimal.Parse(masker.Mask("1000.0000"), CultureInfo.InvariantCulture) / 1000;

        Assert.Equal(a, b, 4);
        Assert.Equal(a, c, 4);
    }

    [Fact]
    public void PerRow_mode_varies_by_row()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb);

        var outputs = Enumerable.Range(0, 20).Select(_ => masker.Mask("1000.00")).ToHashSet();

        Assert.True(outputs.Count > 10);
    }

    [Fact]
    public void Unparseable_values_are_redacted_and_counted()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb);
        masker.Mask("100");

        Assert.Equal(RedactOptions.DefaultText, masker.Mask("N/A"));
        Assert.Equal(1, masker.Verification.RedactedUnmaskableCount);
        Assert.Contains(masker.Verification.Warnings, w => w.Contains("couldn't be masked"));
    }

    [Fact]
    public void Verification_reports_sums()
    {
        var masker = new TestMasker(MaskingStrategy.Perturb, new PerturbOptions(Mode: PerturbMode.Global));
        foreach (var v in new[] { "100.00", "200.00", "300.00" })
            masker.Mask(v);

        var verification = masker.Verification;

        Assert.Equal(600m, verification.SourceSum);
        Assert.NotNull(verification.SumDifferencePercent);
        Assert.InRange(Math.Abs(verification.SumDifferencePercent!.Value), 0m, 15m);
    }
}
