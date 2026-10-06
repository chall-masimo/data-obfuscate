using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class ValuePatternsTests
{
    [Theory]
    [InlineData("123", true, 0, false, false, false)]
    [InlineData("-12.50", false, 2, false, false, false)]
    [InlineData("$1,234.50", false, 2, true, true, false)]
    [InlineData("(12.00)", false, 2, false, false, true)]
    [InlineData("($1,000)", true, 0, true, true, true)]
    [InlineData("-$5", true, 0, false, true, false)]
    [InlineData(".75", false, 2, false, false, false)]
    [InlineData(" 42 ", true, 0, false, false, false)]
    public void Parses_us_numbers(string value, bool isInteger, int scale, bool thousands, bool currency, bool parens)
    {
        Assert.True(ValuePatterns.TryParseNumber(value, out var shape));
        Assert.Equal(new NumberShape(isInteger, scale, thousands, currency, parens), shape);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.")]
    [InlineData("1,23")]        // decimal comma: not a US number
    [InlineData("12,34,567")]
    [InlineData("(12")]
    [InlineData("1e5")]
    [InlineData("abc")]
    public void Rejects_non_numbers(string value)
    {
        Assert.False(ValuePatterns.TryParseNumber(value, out _));
    }

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("first.last+tag@sub.example.org", true)]
    [InlineData("no-at-sign.com", false)]
    [InlineData("a@b", false)]
    [InlineData("a b@c.com", false)]
    public void Email(string value, bool expected) => Assert.Equal(expected, ValuePatterns.IsEmail(value));

    [Theory]
    [InlineData("(555) 123-4567", true)]
    [InlineData("555-123-4567", true)]
    [InlineData("555.123.4567", true)]
    [InlineData("+1 555 123 4567", true)]
    [InlineData("555-123-4567 x89", true)]
    [InlineData("5551234567", false)]   // bare digits could be an ID
    [InlineData("123-45-6789", false)]  // SSN shape
    public void Formatted_phone(string value, bool expected) => Assert.Equal(expected, ValuePatterns.IsFormattedPhone(value));

    [Theory]
    [InlineData("5551234567", true)]
    [InlineData("555-1234", true)]
    [InlineData("12345", false)]
    [InlineData("call me", false)]
    public void Phone_like(string value, bool expected) => Assert.Equal(expected, ValuePatterns.IsPhoneLike(value));

    [Fact]
    public void Zip_shapes()
    {
        Assert.True(ValuePatterns.IsZip5("02134"));
        Assert.True(ValuePatterns.IsZipPlus4("02134-1234"));
        Assert.True(ValuePatterns.IsShortZip("2134"));
        Assert.False(ValuePatterns.IsZip5("2134a"));
    }

    [Fact]
    public void Leading_zero()
    {
        Assert.True(ValuePatterns.HasLeadingZero("007"));
        Assert.False(ValuePatterns.HasLeadingZero("0"));
        Assert.False(ValuePatterns.HasLeadingZero("0.5"));
        Assert.False(ValuePatterns.HasLeadingZero("70"));
    }

    [Theory]
    [InlineData("CA", true)]
    [InlineData("ny", true)]
    [InlineData("New York", true)]
    [InlineData("ZZ", false)]
    [InlineData("Cal", false)]
    public void States(string value, bool expected) => Assert.Equal(expected, UsStates.IsState(value));
}
