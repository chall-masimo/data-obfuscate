using System.Globalization;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class TypeDetectorTests
{
    private const int Rows = 200;
    private static readonly DateTime Start = new(2024, 1, 1);
    private static readonly string[] States = ["CA", "NY", "TX", "FL", "WA", "OR", "AZ", "NV"];
    private static readonly string[] ServiceLines = ["Cardiology", "Oncology", "Orthopedics", "Neurology", "Primary Care"];

    private static string[] Gen(Func<int, string> value) => Enumerable.Range(0, Rows).Select(value).ToArray();

    private static string Date(int i, string format, int hours = 0) =>
        Start.AddDays(i).AddHours(hours).ToString(format, CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, (string Column, string?[] Values, DetectedType Expected)> Cases = new()
    {
        ["empty"] = ("Notes", [null, null, "", "  ", null], DetectedType.Empty),
        ["email"] = ("Contact", Gen(i => $"user{i}@example.com"), DetectedType.Email),

        ["zip-with-hint"] = ("Zip", Gen(i => (1000 + i * 37).ToString("D5")), DetectedType.Zip),
        ["zip-stripped-zeros"] = ("Postal_Code", Gen(i => i % 2 == 0 ? (1000 + i).ToString() : (10000 + i).ToString()), DetectedType.Zip),
        ["zip-plus4-without-hint"] = ("Ship_To", Gen(i => $"{10000 + i}-{1000 + i}"), DetectedType.Zip),
        ["5-digit-account-no-is-not-zip"] = ("Account_No", Gen(i => (10000 + i * 3).ToString()), DetectedType.Identifier),

        ["phone-hint-bare-digits"] = ("Phone_Number", Gen(i => $"555{1000000 + i}"), DetectedType.Phone),
        ["phone-formatted-no-hint"] = ("Primary", Gen(i => $"({200 + i % 700}) {100 + i % 800}-{1000 + i}"), DetectedType.Phone),
        ["bare-10-digits-is-not-phone"] = ("Ref", Gen(i => (5551000000L + i * 7).ToString()), DetectedType.Identifier),

        ["iso-date"] = ("Order_Date", Gen(i => Date(i, "yyyy-MM-dd")), DetectedType.Date),
        ["iso-datetime"] = ("Created", Gen(i => Date(i, "yyyy-MM-dd HH:mm:ss", hours: i % 24)), DetectedType.DateTime),
        ["us-date"] = ("Ship", Gen(i => Date(i, "M/d/yyyy")), DetectedType.Date),
        ["us-datetime-ampm"] = ("Visit", Gen(i => Date(i, "M/d/yyyy h:mm tt", hours: i % 24)), DetectedType.DateTime),
        ["named-month"] = ("Booked", Gen(i => Date(i, "dd-MMM-yyyy")), DetectedType.Date),
        ["yyyymmdd-with-date-hint"] = ("Invoice_Date", Gen(i => Date(i, "yyyyMMdd")), DetectedType.Date),
        ["yyyymmdd-without-hint-is-id"] = ("Batch", Gen(i => Date(i, "yyyyMMdd")), DetectedType.Identifier),

        ["boolean-yn"] = ("Is_Active", Gen(i => i % 2 == 0 ? "Y" : "N"), DetectedType.Boolean),
        ["boolean-mixed-case"] = ("Status", Gen(i => (i % 3) switch { 0 => "Yes", 1 => "no", _ => "YES" }), DetectedType.Boolean),
        ["boolean-0-1"] = ("Flag", Gen(i => (i % 2).ToString()), DetectedType.Boolean),

        ["state-with-hint"] = ("State", Gen(i => States[i % 3]), DetectedType.State),
        ["state-without-hint"] = ("Region", Gen(i => States[i % 8]), DetectedType.State),

        ["id-hint-low-cardinality"] = ("Region_ID", Gen(i => (i % 3 + 1).ToString()), DetectedType.Identifier),
        ["code-low-cardinality-is-categorical"] = ("Region_Code", Gen(i => $"R{i % 6}"), DetectedType.Categorical),
        ["code-high-cardinality"] = ("Product_Code", Gen(i => $"P{i}"), DetectedType.Identifier),
        ["zero-padded-codes"] = ("Store", Gen(i => (i % 120).ToString("D4")), DetectedType.Identifier),
        ["numeric-customer-id-is-not-measure"] = ("Customer_ID", Gen(i => (i + 1).ToString()), DetectedType.Identifier),
        ["unhinted-unique-integers"] = ("Account", Gen(i => (1000 + i).ToString()), DetectedType.Identifier),

        ["owner-name-is-person"] = ("Account_Owner_Name", Gen(i => $"Person {i}"), DetectedType.PersonName),
        ["first-name"] = ("FirstName", Gen(i => $"First{i}"), DetectedType.PersonName),
        ["customer-name-is-org"] = ("Customer_Name", Gen(i => $"Acme {i}"), DetectedType.OrgName),
        ["hospital-name"] = ("Hospital_Name", Gen(i => $"General {i}"), DetectedType.OrgName),
        ["city"] = ("City", Gen(i => $"Town {(char)('A' + i % 26)}"), DetectedType.City),
        ["address"] = ("Address1", Gen(i => $"{i} Main St"), DetectedType.Address),

        ["count-with-hint-beats-near-unique"] = ("Number_of_Beds", Gen(i => (10 + i).ToString()), DetectedType.Count),
        ["qty"] = ("Qty", Gen(i => (i % 20 + 1).ToString()), DetectedType.Count),
        ["amount-integers"] = ("Amount", Gen(i => (i * 13 % 5000).ToString()), DetectedType.Measure),
        ["unhinted-decimals"] = ("Val", Gen(i => (i * 1.37).ToString("F2", CultureInfo.InvariantCulture)), DetectedType.Measure),
        ["revenue-currency"] = ("Revenue", Gen(i => (i * 123.45m).ToString("$#,##0.00", CultureInfo.InvariantCulture)), DetectedType.Measure),
        ["unhinted-integers-medium-cardinality"] = ("Score", Gen(i => (i % 150).ToString()), DetectedType.Measure),

        ["categorical"] = ("Service_Line", Gen(i => ServiceLines[i % 5]), DetectedType.Categorical),
        ["year-is-categorical"] = ("Year", Gen(i => (2015 + i % 10).ToString()), DetectedType.Categorical),

        ["long-text"] = ("Comments", Gen(i => $"Customer called about invoice {i} and asked for a callback about the delivery schedule."), DetectedType.FreeText),
        ["short-text-with-spaces"] = ("Product_Desc", Gen(i => $"Widget model {i}"), DetectedType.FreeText),
        ["fallback"] = ("Misc", Gen(i => $"item{i}"), DetectedType.Text),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Detects_type(string caseName)
    {
        var (column, values, expected) = Cases[caseName];

        var profile = CsvProfiler.ProfileColumn(column, values);

        Assert.True(expected == profile.Type, $"Expected {expected} but got {profile.Type} ({profile.Reason}).");
    }

    [Fact]
    public void Stripped_zip_zeros_are_warned_about()
    {
        var profile = Profile("Postal_Code", Gen(i => i % 2 == 0 ? (1000 + i).ToString() : (10000 + i).ToString()));

        Assert.Contains(profile.Warnings, w => w.Contains("leading zeros"));
    }

    [Fact]
    public void Ambiguous_day_month_assumes_us_and_warns()
    {
        var profile = Profile("Visit", Gen(i => $"{i % 12 + 1}/{i / 12 % 12 + 1}/2024"));

        Assert.Equal(DetectedType.Date, profile.Type);
        Assert.Equal("M/d/yyyy", profile.DateFormats[0]);
        Assert.Contains(profile.Warnings, w => w.Contains("month/day"));
    }

    [Fact]
    public void Day_over_12_settles_us_order_without_warning()
    {
        var profile = Profile("Ship", Gen(i => Date(i, "M/d/yyyy")));

        Assert.Equal(["M/d/yyyy"], profile.DateFormats);
        Assert.Empty(profile.Warnings);
    }

    [Fact]
    public void Day_first_dates_are_recognised()
    {
        var profile = Profile("Ship", Gen(i => Date(i, "d/M/yyyy")));

        Assert.Equal(["d/M/yyyy"], profile.DateFormats);
        Assert.Empty(profile.Warnings);
    }

    [Fact]
    public void Mixed_date_formats_are_all_recorded()
    {
        var profile = Profile("Ship", Gen(i => i % 2 == 0 ? Date(i, "yyyy-MM-dd") : Date(i, "M/d/yyyy")));

        Assert.Equal(DetectedType.Date, profile.Type);
        Assert.Equivalent(new[] { "yyyy-MM-dd", "M/d/yyyy" }, profile.DateFormats, strict: true);
    }

    [Fact]
    public void A_few_bad_values_are_tolerated()
    {
        var values = Gen(i => i % 50 == 0 ? "unknown" : $"user{i}@example.com"); // 2% bad

        Assert.Equal(DetectedType.Email, Profile("Contact", values).Type);
    }

    [Fact]
    public void Reasons_and_warnings_contain_no_values()
    {
        foreach (var (column, values, _) in Cases.Values)
        {
            var profile = CsvProfiler.ProfileColumn(column, values);
            foreach (var value in values.Where(v => v is { Length: > 2 }))
            {
                Assert.DoesNotContain(value!, profile.Reason);
                Assert.All(profile.Warnings, w => Assert.DoesNotContain(value!, w));
            }
        }
    }

    private static ColumnProfile Profile(string column, string?[] values) => CsvProfiler.ProfileColumn(column, values);
}
