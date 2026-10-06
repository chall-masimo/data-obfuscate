using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CsvMasker.Core.Masking;

/// <summary>
/// Derives per-value seeds: <c>HMACSHA256(jobKey, domain ␟ value [␟ suffix])</c>, where ␟ is U+001F.
/// The same job key, domain and value always give the same seed, so masking is consistent
/// within a file. A fresh key per job means nothing carries over between runs.
/// </summary>
internal sealed class SeedSource
{
    /// <summary>Value used for column-wide (Global mode) seeds; U+001E can't collide with a real value's seed layout.</summary>
    public const string GlobalValue = "\u001Eglobal";

    private const int StackLimit = 512;
    private readonly byte[] _key;

    public SeedSource(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("The job key must be 32 bytes.", nameof(key));
        _key = key;
    }

    public SeededRandom Random(string domain, string value, string? suffix = null)
    {
        Span<byte> hash = stackalloc byte[32];
        Derive(domain, value, suffix, hash);
        return new SeededRandom(hash);
    }

    public int Int32(string domain, string value, string? suffix = null)
    {
        Span<byte> hash = stackalloc byte[32];
        Derive(domain, value, suffix, hash);
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }

    public void Derive(string domain, string value, string? suffix, Span<byte> destination)
    {
        int max = Encoding.UTF8.GetMaxByteCount(domain.Length + value.Length + (suffix?.Length ?? 0) + 2);
        byte[]? rented = max > StackLimit ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buffer = rented is not null ? rented : stackalloc byte[StackLimit];
        try
        {
            int n = Encoding.UTF8.GetBytes(domain, buffer);
            buffer[n++] = 0x1F;
            n += Encoding.UTF8.GetBytes(value, buffer[n..]);
            if (suffix is not null)
            {
                buffer[n++] = 0x1F;
                n += Encoding.UTF8.GetBytes(suffix, buffer[n..]);
            }
            HMACSHA256.HashData(_key, buffer[..n], destination);
        }
        finally
        {
            // The buffer held a source value; don't leave it in a pooled array.
            buffer.Clear();
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>xoshiro256** seeded from a 32-byte HMAC. Small, fast and fully determined by its seed.</summary>
internal struct SeededRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public SeededRandom(ReadOnlySpan<byte> seed)
    {
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(seed);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]);
        _s2 = BinaryPrimitives.ReadUInt64LittleEndian(seed[16..]);
        _s3 = BinaryPrimitives.ReadUInt64LittleEndian(seed[24..]);
        if ((_s0 | _s1 | _s2 | _s3) == 0)
            _s0 = 1;
    }

    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, maxExclusive).</summary>
    public int Next(int maxExclusive) => (int)(((NextUInt64() >> 32) * (ulong)maxExclusive) >> 32);

    /// <summary>Uniform in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
