using System.Numerics;

namespace CsvMasker.Core.Profiling;

/// <summary>
/// Counts distinct strings exactly up to a limit, then switches to a HyperLogLog sketch
/// (2^14 one-byte registers = 16 KB, ~0.8% standard error), so memory per column stays bounded
/// however many distinct values the sample holds.
/// </summary>
internal sealed class DistinctCounter
{
    private const int Precision = 14;
    private const int RegisterCount = 1 << Precision;

    private readonly int _exactLimit;
    private HashSet<string>? _exact = new(StringComparer.Ordinal);
    private byte[]? _registers;

    public DistinctCounter(int exactLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exactLimit);
        _exactLimit = exactLimit;
    }

    public bool IsExact => _exact is not null;

    /// <summary>The distinct values, while counting is still exact; null after switching to the sketch.</summary>
    public IReadOnlySet<string>? ExactValues => _exact;

    public long Count => _exact?.Count ?? Estimate();

    public void Add(string value)
    {
        if (_exact is null)
        {
            AddToSketch(value);
            return;
        }

        if (!_exact.Add(value) || _exact.Count <= _exactLimit)
            return;

        _registers = new byte[RegisterCount];
        foreach (var v in _exact)
            AddToSketch(v);
        _exact = null;
    }

    private void AddToSketch(string value)
    {
        ulong hash = Hash(value);
        int index = (int)(hash >> (64 - Precision));
        ulong rest = hash << Precision;
        byte rank = (byte)(rest == 0 ? 64 - Precision + 1 : BitOperations.LeadingZeroCount(rest) + 1);
        if (rank > _registers![index])
            _registers[index] = rank;
    }

    private long Estimate()
    {
        double sum = 0;
        int zeros = 0;
        foreach (byte rank in _registers!)
        {
            sum += Math.ScaleB(1.0, -rank);
            if (rank == 0)
                zeros++;
        }

        const double m = RegisterCount;

        // Linear counting is accurate (≤ ~2% error) up to about 5m and avoids raw HyperLogLog's
        // bias at low cardinalities, so use it while it applies.
        if (zeros > 0)
        {
            double linear = m * Math.Log(m / zeros);
            if (linear <= 5 * m)
                return (long)Math.Round(linear);
        }

        double alpha = 0.7213 / (1 + 1.079 / m);
        return (long)Math.Round(alpha * m * m / sum);
    }

    /// <summary>FNV-1a over UTF-16 code units, then a splitmix64 finaliser so every bit is well mixed.</summary>
    internal static ulong Hash(string value)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in value)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return h;
    }
}
