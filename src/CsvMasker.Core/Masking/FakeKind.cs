namespace CsvMasker.Core.Masking;

/// <summary>What kind of value the <see cref="MaskingStrategy.Fake"/> strategy generates.</summary>
public enum FakeKind
{
    PersonFirst,
    PersonLast,
    PersonFull,
    Company,
    Hospital,
    Email,
    Phone,
    StreetAddress,
    City,
}
