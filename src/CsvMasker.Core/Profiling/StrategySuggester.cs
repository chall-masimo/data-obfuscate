using CsvMasker.Core.Masking;

namespace CsvMasker.Core.Profiling;

/// <summary>A pre-filled strategy for the review screen. The user still confirms every column, and Keep is always allowed.</summary>
public sealed record StrategySuggestion(MaskingStrategy Strategy, FakeKind? FakeKind = null)
{
    public override string ToString() => FakeKind is { } kind ? $"{Strategy}({kind})" : Strategy.ToString();
}

internal static class StrategySuggester
{
    public static StrategySuggestion Suggest(DetectedType type, ColumnNameHints hints) => type switch
    {
        DetectedType.Identifier => new(MaskingStrategy.HashId),
        DetectedType.Zip => new(MaskingStrategy.ZipRemap),
        DetectedType.Email => new(MaskingStrategy.Fake, FakeKind.Email),
        DetectedType.Phone => new(MaskingStrategy.Fake, FakeKind.Phone),
        DetectedType.PersonName => new(MaskingStrategy.Fake,
            hints.IsFirstName ? FakeKind.PersonFirst : hints.IsLastName ? FakeKind.PersonLast : FakeKind.PersonFull),
        DetectedType.OrgName => new(MaskingStrategy.Fake, hints.IsHospital ? FakeKind.Hospital : FakeKind.Company),
        DetectedType.Address => new(MaskingStrategy.Fake, FakeKind.StreetAddress),
        DetectedType.City => new(MaskingStrategy.Fake, FakeKind.City),
        DetectedType.State => new(MaskingStrategy.Keep),
        DetectedType.Date or DetectedType.DateTime => new(MaskingStrategy.DateShift),
        DetectedType.Measure or DetectedType.Count => new(MaskingStrategy.Perturb),
        DetectedType.Boolean or DetectedType.Categorical => new(MaskingStrategy.Keep),
        DetectedType.FreeText or DetectedType.Text => new(MaskingStrategy.Redact),
        DetectedType.Empty => new(MaskingStrategy.Keep),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
