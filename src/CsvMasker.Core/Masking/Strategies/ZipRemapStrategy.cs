using System.Globalization;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Masking.Strategies;

/// <summary>Real US ZIP codes by 3-digit prefix. A licensed list can be plugged in here later (CLAUDE.md open item).</summary>
public interface IZipReference
{
    /// <summary>True when ZIPs come from a real list, so they should geocode.</summary>
    bool IsRealList { get; }

    /// <summary>5-digit ZIPs starting with <paramref name="prefix"/>, or null/empty when unknown.</summary>
    IReadOnlyList<string>? ZipsWithPrefix(string prefix);
}

/// <summary>No list: ZIPs keep their 3-digit prefix and the remaining digits are derived.</summary>
public sealed class FallbackZipReference : IZipReference
{
    public static FallbackZipReference Instance { get; } = new();

    public bool IsRealList => false;

    public IReadOnlyList<string>? ZipsWithPrefix(string prefix) => null;
}

/// <summary>
/// Maps a ZIP to another with the same 3-digit prefix. Keeps the shape: 5 digits, ZIP+4, or a
/// 3–4 digit ZIP whose leading zeros were stripped upstream.
/// </summary>
internal sealed class ZipRemapStrategy(SeedSource seeds, IZipReference reference) : IMaskingStrategy
{
    private const int ListAttempts = 20;

    public bool IsMapping => true;

    public string? Warning => reference.IsRealList
        ? null
        : "ZIPs were derived without a reference list (first 3 digits kept); they may not geocode.";

    public string? Mask(in MaskInput input, int attempt)
    {
        string value = input.Value;
        string? plus4 = null;
        string zip;
        if (ValuePatterns.IsZipPlus4(value))
        {
            zip = value[..5];
            plus4 = value[6..];
        }
        else if (ValuePatterns.IsZip5(value) || ValuePatterns.IsShortZip(value))
        {
            zip = value.PadLeft(5, '0');
        }
        else
        {
            return null;
        }

        var random = seeds.Random(input.SeedDomain, input.SeedValue, attempt == 0 ? null : attempt.ToString(CultureInfo.InvariantCulture));
        string prefix = zip[..3];
        var known = reference.ZipsWithPrefix(prefix);
        string masked = known is { Count: > 0 } && attempt < ListAttempts
            ? known[random.Next(known.Count)]
            : prefix + random.Next(100).ToString("D2", CultureInfo.InvariantCulture);

        if (plus4 is not null)
            return $"{masked}-{random.Next(10_000):D4}";

        // A stripped ZIP keeps its stripped width; the stripped digits are zeros of the shared prefix.
        return masked[(5 - value.Length)..];
    }
}
