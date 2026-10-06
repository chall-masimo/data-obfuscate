using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class StrategySuggesterTests
{
    [Theory]
    [InlineData(DetectedType.Identifier, "Customer_ID", MaskingStrategy.HashId, null)]
    [InlineData(DetectedType.Zip, "Zip", MaskingStrategy.ZipRemap, null)]
    [InlineData(DetectedType.Email, "Email", MaskingStrategy.Fake, FakeKind.Email)]
    [InlineData(DetectedType.Phone, "Phone", MaskingStrategy.Fake, FakeKind.Phone)]
    [InlineData(DetectedType.PersonName, "First_Name", MaskingStrategy.Fake, FakeKind.PersonFirst)]
    [InlineData(DetectedType.PersonName, "LastName", MaskingStrategy.Fake, FakeKind.PersonLast)]
    [InlineData(DetectedType.PersonName, "Account_Owner_Name", MaskingStrategy.Fake, FakeKind.PersonFull)]
    [InlineData(DetectedType.OrgName, "Customer_Name", MaskingStrategy.Fake, FakeKind.Company)]
    [InlineData(DetectedType.OrgName, "Facility_Name", MaskingStrategy.Fake, FakeKind.Hospital)]
    [InlineData(DetectedType.Address, "Address1", MaskingStrategy.Fake, FakeKind.StreetAddress)]
    [InlineData(DetectedType.City, "City", MaskingStrategy.Fake, FakeKind.City)]
    [InlineData(DetectedType.State, "State", MaskingStrategy.Keep, null)]
    [InlineData(DetectedType.Date, "Order_Date", MaskingStrategy.DateShift, null)]
    [InlineData(DetectedType.DateTime, "Created", MaskingStrategy.DateShift, null)]
    [InlineData(DetectedType.Measure, "Amount", MaskingStrategy.Perturb, null)]
    [InlineData(DetectedType.Count, "Qty", MaskingStrategy.Perturb, null)]
    [InlineData(DetectedType.Boolean, "Is_Active", MaskingStrategy.Keep, null)]
    [InlineData(DetectedType.Categorical, "Region", MaskingStrategy.Keep, null)]
    [InlineData(DetectedType.FreeText, "Comments", MaskingStrategy.Redact, null)]
    [InlineData(DetectedType.Text, "Misc", MaskingStrategy.Redact, null)]
    [InlineData(DetectedType.Empty, "Unused", MaskingStrategy.Keep, null)]
    public void Suggests_strategy(DetectedType type, string column, MaskingStrategy strategy, FakeKind? kind)
    {
        var suggestion = StrategySuggester.Suggest(type, new ColumnNameHints(column));

        Assert.Equal(new StrategySuggestion(strategy, kind), suggestion);
    }

    [Fact]
    public void Every_type_has_a_suggestion()
    {
        foreach (var type in Enum.GetValues<DetectedType>())
            StrategySuggester.Suggest(type, new ColumnNameHints("x"));
    }
}
