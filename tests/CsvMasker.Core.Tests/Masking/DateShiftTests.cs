using System.Globalization;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Masking;

public class DateShiftTests
{
    private static readonly DateTime Sample = new(2024, 3, 5, 14, 7, 9, 120, DateTimeKind.Utc);

    public static TheoryData<string> AllFormats => new(DateMatcher.Formats.Select(f => f.Pattern));

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Every_candidate_format_keeps_its_exact_shape(string format)
    {
        string source = Sample.ToString(format, CultureInfo.InvariantCulture);
        var strategy = Strategy(new DateShiftOptions(Formats: [format]));
        var masker = new TestMasker(new ColumnRule(MaskingStrategy.DateShift, new DateShiftOptions(Formats: [format])));

        string masked = masker.Mask(source);

        var parsedSource = DateTime.ParseExact(source, format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var parsedMasked = DateTime.ParseExact(masked, format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(masked, parsedMasked.ToString(format, CultureInfo.InvariantCulture));
        Assert.Equal(strategy.OffsetDays, (parsedMasked.Date - parsedSource.Date).Days);
        Assert.Equal(parsedSource.TimeOfDay, parsedMasked.TimeOfDay);
    }

    [Theory]
    [InlineData("01/05/2024", "M/d/yyyy", @"^\d\d/\d\d/\d{4}$")]          // zero padding kept
    [InlineData("1/5/2024", "M/d/yyyy", @"^\d{1,2}/\d{1,2}/\d{4}$")]
    [InlineData("05-JAN-2024", "d-MMM-yyyy", @"^\d\d-[A-Z]{3}-\d{4}$")]   // month-name case kept
    [InlineData("2024-01-15T10:30:00.1230000+05:00", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", @"^\d{4}-\d\d-\d\dT10:30:00\.1230000\+05:00$")]
    [InlineData("2024-01-15T10:30:00Z", "yyyy-MM-ddTHH:mm:ssK", @"^\d{4}-\d\d-\d\dT10:30:00Z$")]
    public void Keeps_source_text_outside_the_date(string source, string format, string pattern)
    {
        string masked = new TestMasker(MaskingStrategy.DateShift, new DateShiftOptions(Formats: [format])).Mask(source);

        Assert.Matches(pattern, masked);
        Assert.NotEqual(source, masked);
    }

    [Fact]
    public void Day_first_profile_format_is_respected()
    {
        // 03/04/2025 is 3 April under d/M/yyyy; the shift must be applied to that date.
        var options = new DateShiftOptions(Formats: ["d/M/yyyy"]);
        var strategy = Strategy(options);
        string masked = new TestMasker(MaskingStrategy.DateShift, options).Mask("03/04/2025");

        var expected = new DateTime(2025, 4, 3).AddDays(strategy.OffsetDays);
        Assert.Equal(expected.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), masked);
    }

    [Fact]
    public void Offset_is_within_range_and_never_zero()
    {
        var options = new DateShiftOptions(MaxDays: 10);
        for (byte b = 0; b < 200; b++)
        {
            int offset = Strategy(options, key: Key(b)).OffsetDays;
            Assert.InRange(Math.Abs(offset), 1, 10);
        }
    }

    [Fact]
    public void Keep_weekday_shifts_whole_weeks()
    {
        var options = new DateShiftOptions(MaxDays: 30, KeepWeekday: true);
        for (byte b = 0; b < 50; b++)
        {
            string masked = new TestMasker(MaskingStrategy.DateShift, options, Key(b)).Mask("2024-03-05");
            var date = DateTime.ParseExact(masked, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Equal(DayOfWeek.Tuesday, date.DayOfWeek);
            Assert.NotEqual("2024-03-05", masked);
        }
    }

    [Fact]
    public void Unparseable_dates_are_redacted_and_counted()
    {
        var masker = new TestMasker(MaskingStrategy.DateShift);

        Assert.Equal(RedactOptions.DefaultText, masker.Mask("TBD"));
        Assert.Equal(RedactOptions.DefaultText, masker.Mask("2024-02-30"));
        Assert.Equal(2, masker.Verification.RedactedUnmaskableCount);
    }

    private static byte[] Key(byte b) => Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + b)).ToArray();

    private static DateShiftStrategy Strategy(DateShiftOptions options, byte[]? key = null) =>
        new(new SeedSource((byte[])(key ?? TestMasker.KeyA).Clone()), "Col", options);
}
