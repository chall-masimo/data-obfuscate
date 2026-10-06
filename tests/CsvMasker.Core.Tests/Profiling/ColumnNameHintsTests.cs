using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class ColumnNameHintsTests
{
    [Theory]
    [InlineData("CustomerID", new[] { "customer", "id" })]
    [InlineData("customerId", new[] { "customer", "id" })]
    [InlineData("IDNumber", new[] { "id", "number" })]
    [InlineData("zip_code", new[] { "zip", "code" })]
    [InlineData("Number_of_Beds", new[] { "number", "of", "beds" })]
    [InlineData("ShipTo Address1", new[] { "ship", "to", "address", "1" })]
    [InlineData("FIRSTNAME", new[] { "first", "name" })]
    [InlineData("Name", new[] { "name" })]
    [InlineData("  ", new string[0])]
    public void Tokenizes(string name, string[] expected)
    {
        Assert.Equal(expected, ColumnNameHints.Tokenize(name));
    }

    [Theory]
    // Expected values are enum names: the enums are internal, so they can't be public test parameters.
    [InlineData("Customer_ID", "Strong")]
    [InlineData("Account_No", "Strong")]
    [InlineData("OrderKey", "Strong")]
    [InlineData("Product_Code", "Weak")]
    [InlineData("Number_of_Beds", "None")]
    [InlineData("Valid", "None")]          // "id" inside a word is not a hint
    public void Identifier_hint(string name, string expected)
    {
        Assert.Equal(Enum.Parse<IdHint>(expected), new ColumnNameHints(name).IdHint);
    }

    [Theory]
    [InlineData("Account_Owner_Name", "Person")]
    [InlineData("Customer_Contact_Name", "Person")]
    [InlineData("Customer_Name", "Org")]
    [InlineData("VendorName", "Org")]
    [InlineData("Name", null)]
    [InlineData("Customer", null)]
    public void Person_or_org(string name, string? expected)
    {
        Assert.Equal(expected, new ColumnNameHints(name).NameKindHint?.Kind.ToString());
    }

    [Fact]
    public void Substring_hints_only_for_distinctive_words()
    {
        Assert.True(new ColumnNameHints("BillingZipCode").IsZip);
        Assert.True(new ColumnNameHints("HomeTelephone").IsPhone);
        Assert.False(new ColumnNameHints("Capacity").IsCity);
        Assert.False(new ColumnNameHints("Statement").IsState);
    }
}
