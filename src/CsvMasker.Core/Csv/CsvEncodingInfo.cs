using System.Text;

namespace CsvMasker.Core.Csv;

/// <summary>
/// A text encoding plus the exact byte order mark found in the source (empty when there was none).
/// Every encoding produced here throws on invalid input instead of substituting replacement
/// characters, so a file that can't round-trip fails loudly rather than being silently corrupted.
/// </summary>
public sealed record CsvEncodingInfo(Encoding Encoding, byte[] Preamble, string Name)
{
    static CsvEncodingInfo() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static CsvEncodingInfo Utf8(bool withBom) => new(
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        withBom ? [0xEF, 0xBB, 0xBF] : [],
        withBom ? "UTF-8 (BOM)" : "UTF-8");

    public static CsvEncodingInfo Utf16(bool bigEndian, bool withBom)
    {
        byte[] bom = !withBom ? [] : bigEndian ? [0xFE, 0xFF] : [0xFF, 0xFE];
        return new(
            new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true),
            bom,
            $"UTF-16 {(bigEndian ? "BE" : "LE")}{(withBom ? " (BOM)" : "")}");
    }

    /// <summary>UTF-32 is only recognised by its BOM, so the BOM is always part of it.</summary>
    public static CsvEncodingInfo Utf32(bool bigEndian) => new(
        new UTF32Encoding(bigEndian, byteOrderMark: false, throwOnInvalidCharacters: true),
        bigEndian ? [0x00, 0x00, 0xFE, 0xFF] : [0xFF, 0xFE, 0x00, 0x00],
        $"UTF-32 {(bigEndian ? "BE" : "LE")} (BOM)");

    public static CsvEncodingInfo Windows1252 => new(
        Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        [],
        "Windows-1252");
}
