using System.Text;
using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class EncodingDetectionTests
{
    private const string Text = "Id,Name\r\n1,Zoë\r\n2,José\r\n";

    public static TheoryData<string> EncodingNames =>
    [
        "UTF-8 (BOM)", "UTF-8", "Windows-1252", "UTF-16 LE (BOM)", "UTF-16 BE (BOM)",
        "UTF-16 LE", "UTF-16 BE", "UTF-32 LE (BOM)", "UTF-32 BE (BOM)",
    ];

    private static CsvEncodingInfo ByName(string name) => name switch
    {
        "UTF-8 (BOM)" => Utf8Bom,
        "UTF-8" => Utf8,
        "Windows-1252" => Win1252,
        "UTF-16 LE (BOM)" => Utf16LeBom,
        "UTF-16 BE (BOM)" => Utf16BeBom,
        "UTF-16 LE" => Utf16Le,
        "UTF-16 BE" => Utf16Be,
        "UTF-32 LE (BOM)" => Utf32LeBom,
        "UTF-32 BE (BOM)" => CsvEncodingInfo.Utf32(bigEndian: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(EncodingNames))]
    public void Detects_encoding(string name)
    {
        var expected = ByName(name);

        var detected = Detect(Encode(Text, expected));

        Assert.Equal(name, detected.Name);
        Assert.Equal(expected.Preamble, detected.Preamble);
    }

    [Fact]
    public void Pure_ascii_is_reported_as_utf8()
    {
        Assert.Equal("UTF-8", Detect("a,b\r\n1,2\r\n"u8.ToArray()).Name);
    }

    [Fact]
    public void Empty_file_is_utf8()
    {
        Assert.Equal("UTF-8", Detect([]).Name);
    }

    [Fact]
    public void Windows1252_byte_far_past_the_start_is_still_found()
    {
        // 300 KB of ASCII, then one 1252 'é' (0xE9), which is invalid UTF-8.
        var ascii = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890,abcdefghi\r\n", 15_000)));
        byte[] input = [.. "a,b\r\n"u8, .. ascii, 0xE9, .. "\r\n"u8];

        Assert.Equal("Windows-1252", Detect(input).Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Utf8_sequence_split_across_read_chunks_is_valid(int bytesBeforeBoundary)
    {
        // Place the 3-byte '€' so it straddles the detector's chunk boundary.
        int prefixLength = CsvEncodingDetector.ChunkSize - bytesBeforeBoundary;
        byte[] input = [.. Enumerable.Repeat((byte)'a', prefixLength), .. "€\r\n"u8];

        Assert.Equal("UTF-8", Detect(input).Name);
    }

    [Fact]
    public void Truncated_utf8_sequence_at_end_of_file_is_not_utf8()
    {
        byte[] input = [.. "a,b\r\n"u8, 0xE2, 0x82]; // first two bytes of '€'

        Assert.Equal("Windows-1252", Detect(input).Name);
    }

    [Fact]
    public void Restores_stream_position()
    {
        using var stream = new MemoryStream(Encode(Text, Utf8Bom));

        CsvEncodingDetector.Detect(stream);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Rejects_non_seekable_stream()
    {
        using var stream = new NonSeekableStream(new MemoryStream("a,b\r\n"u8.ToArray()));

        Assert.Throws<ArgumentException>(() => CsvEncodingDetector.Detect(stream));
    }

    private static CsvEncodingInfo Detect(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return CsvEncodingDetector.Detect(stream);
    }
}
