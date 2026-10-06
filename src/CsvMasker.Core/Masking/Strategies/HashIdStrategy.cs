using System.Globalization;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>
/// Format-preserving replacement: same length; digits become digits, upper-case letters
/// upper-case, lower-case letters lower-case; separators and other characters are kept. Zero
/// padding is kept as a style: a multi-digit run that starts with 0 still starts with 0, and one
/// that doesn't never gains a leading zero.
/// </summary>
/// <remarks>
/// The first <see cref="RandomAttempts"/> attempts are independent random draws. After that the
/// strategy steps through the whole output space from the last draw, so when the space is nearly
/// full (dense sequential IDs) a free value is always found if one exists.
/// </remarks>
internal sealed class HashIdStrategy(SeedSource seeds, string domain, HashIdOptions options) : IMaskingStrategy
{
    internal const int RandomAttempts = 100;

    public bool IsMapping => true;

    public string? Mask(in MaskInput input, int attempt)
    {
        string value = input.Value;
        int draw = Math.Min(attempt, RandomAttempts);
        var random = seeds.Random(domain, value, draw == 0 ? null : draw.ToString(CultureInfo.InvariantCulture));

        var output = value.ToCharArray();
        Span<(int Index, char Base, int Radix)> positions = value.Length <= 256
            ? stackalloc (int, char, int)[value.Length]
            : new (int, char, int)[value.Length];
        int count = 0;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            (char Base, int Radix) slot;
            if (char.IsAsciiDigit(c))
            {
                bool runStart = i == 0 || !char.IsAsciiDigit(value[i - 1]);
                bool multiDigitRun = i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]);
                if (runStart && multiDigitRun && c == '0')
                    continue;                       // zero padding stays
                slot = runStart && multiDigitRun ? ('1', 9) : ('0', 10);
            }
            else if (char.IsLetter(c))
            {
                slot = char.IsUpper(c) ? ('A', 26) : ('a', 26);
            }
            else
            {
                continue;                           // separators and symbols stay
            }

            output[i] = (char)(slot.Base + random.Next(slot.Radix));
            positions[count++] = (i, slot.Base, slot.Radix);
        }

        if (count == 0)
            return null;                            // nothing maskable (e.g. "-"): the caller redacts it

        if (attempt > RandomAttempts)
            Step(output, positions[..count], attempt - RandomAttempts);

        return options.Prefix + new string(output);
    }

    /// <summary>Adds <paramref name="steps"/> to the maskable positions as a mixed-radix number (last position fastest).</summary>
    private static void Step(char[] output, ReadOnlySpan<(int Index, char Base, int Radix)> positions, int steps)
    {
        long carry = steps;
        for (int p = positions.Length - 1; p >= 0 && carry > 0; p--)
        {
            var (index, @base, radix) = positions[p];
            long digit = output[index] - @base + carry;
            output[index] = (char)(@base + (int)(digit % radix));
            carry = digit / radix;
        }
    }
}
