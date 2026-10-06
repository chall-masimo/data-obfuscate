using System.Globalization;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>
/// Multiplies by a factor in [1 - pct, 1 + pct]. Keeps zero, sign, scale, integer-ness and
/// formatting. Count columns never fall below 1 when the source is at least 1.
/// </summary>
internal sealed class PerturbStrategy : IMaskingStrategy
{
    private readonly SeedSource _seeds;
    private readonly string _domain;
    private readonly PerturbOptions _options;
    private readonly decimal _globalFactor;

    public PerturbStrategy(SeedSource seeds, string domain, PerturbOptions options)
    {
        _seeds = seeds;
        _domain = domain;
        _options = options;
        var random = seeds.Random(domain, SeedSource.GlobalValue);
        _globalFactor = Factor(ref random);
    }

    public bool IsMapping => false;

    public string? Mask(in MaskInput input, int attempt)
    {
        if (!NumberFormat.TryParse(input.Value, out var number))
            return null;
        if (number.Value == 0)
            return input.Value;

        decimal factor = _globalFactor;
        if (_options.Mode == PerturbMode.PerRow)
        {
            var random = _seeds.Random(_domain, input.Value, "row:" + input.Row.RecordNumber.ToString(CultureInfo.InvariantCulture));
            factor = Factor(ref random);
        }

        decimal result = Math.Round(number.Value * factor, number.Scale, MidpointRounding.AwayFromZero);

        // Never round a non-zero value to zero (or below 1 for counts that started at 1 or more).
        decimal smallest = number.Scale == 0 ? 1 : 1m / Pow10(number.Scale);
        if (result == 0)
            result = number.Value > 0 ? smallest : -smallest;
        if (_options.IsCount && number.Value >= 1 && result < 1)
            result = 1;

        return number.Format(result);
    }

    private decimal Factor(ref SeededRandom random)
    {
        double pct = _options.Percent;
        double factor = 1 - pct + 2 * pct * random.NextDouble();
        return factor == 1 ? (decimal)(1 + pct / 2) : (decimal)factor;
    }

    private static decimal Pow10(int n)
    {
        decimal p = 1;
        for (int i = 0; i < n; i++) p *= 10;
        return p;
    }
}
