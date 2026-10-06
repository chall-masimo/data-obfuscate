using System.Globalization;
using Bogus;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>Deterministic Bogus values. Each value reseeds one shared Faker from its HMAC seed.</summary>
internal sealed class FakeStrategy(SeedSource seeds, FakeOptions options) : IMaskingStrategy
{
    /// <summary>RFC 2606 reserved domains: they can never receive mail.</summary>
    internal static readonly string[] EmailDomains = ["example.com", "example.net", "example.org"];

    private static readonly string[] HospitalSuffixes = ["Medical Center", "Hospital", "Health"];

    private readonly Faker _faker = new("en");

    public bool IsMapping => true;

    public string? Mask(in MaskInput input, int attempt)
    {
        string? suffix = attempt == 0 ? null : attempt.ToString(CultureInfo.InvariantCulture);
        if (options.Kind == FakeKind.Phone)
        {
            var random = seeds.Random(input.SeedDomain, input.SeedValue, suffix);
            return PhoneFormat.Mask(input.Value, ref random);
        }

        _faker.Random = new Randomizer(seeds.Int32(input.SeedDomain, input.SeedValue, suffix));
        string fake = options.Kind switch
        {
            FakeKind.PersonFirst => _faker.Name.FirstName(),
            FakeKind.PersonLast => LastName(), // draws the first name too, so it matches PersonFull/Email for the same seed
            FakeKind.PersonFull => $"{_faker.Name.FirstName()} {_faker.Name.LastName()}",
            FakeKind.Company => _faker.Company.CompanyName(),
            FakeKind.Hospital => $"{_faker.Address.City()} {_faker.PickRandom(HospitalSuffixes)}",
            FakeKind.Email => Email(),
            FakeKind.StreetAddress => _faker.Address.StreetAddress(),
            FakeKind.City => _faker.Address.City(),
            _ => throw new InvalidOperationException($"Unknown fake kind {options.Kind}."),
        };

        return options.PreserveCase ? CopyCase(input.Value, fake) : fake;
    }

    public string? Disambiguate(string candidate, int n)
    {
        string number = n.ToString(CultureInfo.InvariantCulture);
        if (options.Kind == FakeKind.Email)
        {
            int at = candidate.IndexOf('@');
            return candidate[..at] + number + candidate[at..];
        }
        return options.Kind == FakeKind.Phone ? null : $"{candidate} {number}";
    }

    private string LastName()
    {
        _faker.Name.FirstName();
        return _faker.Name.LastName();
    }

    private string Email()
    {
        string first = _faker.Name.FirstName(), last = _faker.Name.LastName();
        return $"{_faker.Internet.UserName(first, last)}@{_faker.PickRandom(EmailDomains)}".ToLowerInvariant();
    }

    /// <summary>ALL CAPS and all-lower sources produce ALL CAPS and all-lower fakes; anything else is left as generated.</summary>
    internal static string CopyCase(string source, string fake)
    {
        bool hasUpper = false, hasLower = false;
        foreach (char c in source)
        {
            hasUpper |= char.IsUpper(c);
            hasLower |= char.IsLower(c);
        }

        if (hasUpper && !hasLower) return fake.ToUpperInvariant();
        if (hasLower && !hasUpper) return fake.ToLowerInvariant();
        return fake;
    }
}

/// <summary>Replaces a phone number's digits inside its own formatting, keeping NANP validity.</summary>
internal static class PhoneFormat
{
    public static string? Mask(string value, ref SeededRandom random)
    {
        var output = value.ToCharArray();
        int digitCount = value.Count(char.IsAsciiDigit);
        if (digitCount == 0)
            return null;

        // An 11-digit number starting with 1 keeps its country code.
        int seen = 0;
        int nationalStart = digitCount == 11 && value.First(char.IsAsciiDigit) == '1' ? 1 : 0;
        for (int i = 0; i < output.Length; i++)
        {
            if (!char.IsAsciiDigit(output[i]))
                continue;

            int position = seen++ - nationalStart;
            if (position < 0)
                continue;

            // Area code and exchange can't start with 0 or 1 in NANP numbers.
            output[i] = (char)('0' + (position is 0 or 3 ? random.Next(2, 10) : random.Next(10)));
        }
        return new string(output);
    }
}
