using System.Text;

namespace CsvMasker.Core.Csv;

/// <summary>
/// Detects a CSV file's encoding: BOM first, then a BOM-less UTF-16 heuristic, then a full
/// streaming UTF-8 validity scan with Windows-1252 as the fallback.
/// </summary>
public static class CsvEncodingDetector
{
    internal const int ChunkSize = 64 * 1024;
    private const int Utf16SampleBytes = 4 * 1024;

    /// <summary>
    /// Detects the encoding of <paramref name="stream"/>, which must be seekable. The stream is
    /// returned to its original position afterwards.
    /// </summary>
    /// <remarks>
    /// Without a BOM the whole stream is read once: a file whose first non-ASCII byte appears late
    /// would be mis-detected by a sample-only scan, and decoding it with the wrong encoding would
    /// corrupt the output. Memory use is constant regardless of file size.
    /// </remarks>
    public static CsvEncodingInfo Detect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new ArgumentException("Encoding detection needs a seekable stream.", nameof(stream));

        long start = stream.Position;
        try
        {
            var sample = new byte[Utf16SampleBytes];
            int length = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);

            var detected = FromBom(sample.AsSpan(0, length)) ?? FromUtf16Heuristic(sample.AsSpan(0, length));
            if (detected is not null)
                return detected;

            stream.Position = start;
            return IsValidUtf8(stream) ? CsvEncodingInfo.Utf8(withBom: false) : CsvEncodingInfo.Windows1252;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static CsvEncodingInfo? FromBom(ReadOnlySpan<byte> head)
    {
        // UTF-32 LE must be checked before UTF-16 LE: its BOM starts with the UTF-16 LE BOM.
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00])) return CsvEncodingInfo.Utf32(bigEndian: false);
        if (head.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF])) return CsvEncodingInfo.Utf32(bigEndian: true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return CsvEncodingInfo.Utf8(withBom: true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return CsvEncodingInfo.Utf16(bigEndian: false, withBom: true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return CsvEncodingInfo.Utf16(bigEndian: true, withBom: true);
        return null;
    }

    /// <summary>
    /// CSV text is mostly ASCII, so BOM-less UTF-16 shows up as a 0x00 in every other byte:
    /// odd offsets for little-endian, even offsets for big-endian. UTF-8 and 1252 text has none.
    /// </summary>
    private static CsvEncodingInfo? FromUtf16Heuristic(ReadOnlySpan<byte> sample)
    {
        int pairs = sample.Length / 2;
        if (pairs == 0)
            return null;

        int zeroEven = 0, zeroOdd = 0;
        for (int i = 0; i < pairs * 2; i += 2)
        {
            if (sample[i] == 0) zeroEven++;
            if (sample[i + 1] == 0) zeroOdd++;
        }

        if (zeroOdd >= pairs * 0.3 && zeroEven <= pairs * 0.05) return CsvEncodingInfo.Utf16(bigEndian: false, withBom: false);
        if (zeroEven >= pairs * 0.3 && zeroOdd <= pairs * 0.05) return CsvEncodingInfo.Utf16(bigEndian: true, withBom: false);
        return null;
    }

    private static bool IsValidUtf8(Stream stream)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var decoder = encoding.GetDecoder();
        var bytes = new byte[ChunkSize];
        // Large enough that one Convert call always consumes the whole chunk, including any
        // partial sequence the decoder carried over from the previous chunk.
        var chars = new char[encoding.GetMaxCharCount(ChunkSize)];

        try
        {
            int read;
            while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                decoder.Convert(bytes.AsSpan(0, read), chars, flush: false, out _, out _, out _);

            // Flushing throws if the file ends in the middle of a multi-byte sequence.
            decoder.Convert(ReadOnlySpan<byte>.Empty, chars, flush: true, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
