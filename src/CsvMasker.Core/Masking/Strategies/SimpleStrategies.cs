using Bogus;
using System.Text;

namespace CsvMasker.Core.Masking.Strategies;

internal sealed class KeepStrategy : IMaskingStrategy
{
    public bool IsMapping => false;

    public string? Mask(in MaskInput input, int attempt) => input.Value;
}

internal sealed class RedactStrategy(RedactOptions options) : IMaskingStrategy
{
    public bool IsMapping => false;

    public string? Mask(in MaskInput input, int attempt) => options.Text;
}

/// <summary>Deterministic lorem ipsum of exactly the source's length, for layout testing.</summary>
internal sealed class LoremStrategy(SeedSource seeds, string domain) : IMaskingStrategy
{
    private readonly Faker _faker = new("en");

    public bool IsMapping => false;

    public string? Mask(in MaskInput input, int attempt)
    {
        int length = input.Value.Length;
        _faker.Random = new Randomizer(seeds.Int32(domain, input.Value));

        var text = new StringBuilder(length + 16);
        while (text.Length < length)
        {
            if (text.Length > 0)
                text.Append(' ');
            text.Append(_faker.Lorem.Word());
        }

        text.Length = length;
        if (text[^1] == ' ')
            text[^1] = 'a';
        if (char.IsUpper(input.Value[0]))
            text[0] = char.ToUpperInvariant(text[0]);
        return text.ToString();
    }
}
