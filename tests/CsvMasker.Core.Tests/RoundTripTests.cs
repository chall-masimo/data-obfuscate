using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

/// <summary>Every case must come back byte-identical after detect → read → write.</summary>
public class RoundTripTests
{
    // Non-ASCII characters that all exist in Windows-1252 (ë é ö € “ ” map to single bytes there).
    private const string International =
        "Id,Name,City,Note\r\n" +
        "001,Zoë Brontë,Málaga,€5 “deal”\r\n" +
        "002,José,Köln,\r\n";

    private static string WithDelimiter(char d) =>
        $"Id{d}Name{d}Amount\r\n" +
        $"1{d}Alice{d}10.50\r\n" +
        $"2{d}\"Smith{d} Bob\"{d}-3\r\n" +
        $"3{d}{d}\r\n";

    private static readonly Dictionary<string, byte[]> Cases = new()
    {
        // Encodings
        ["utf8-bom"] = Encode(International, Utf8Bom),
        ["utf8-no-bom"] = Encode(International, Utf8),
        ["windows-1252"] = Encode(International, Win1252),
        ["utf16le-bom"] = Encode(International, Utf16LeBom),
        ["utf16be-bom"] = Encode(International, Utf16BeBom),
        ["utf16le-no-bom"] = Encode(International, Utf16Le),
        ["utf16be-no-bom"] = Encode(International, Utf16Be),
        ["utf32le-bom"] = Encode(International, Utf32LeBom),
        ["ascii"] = Encode("a,b\r\n1,2\r\n", Utf8),

        // Delimiters (each includes a quoted field containing the delimiter)
        ["comma"] = Encode(WithDelimiter(','), Utf8),
        ["tab"] = Encode(WithDelimiter('\t'), Utf8),
        ["pipe"] = Encode(WithDelimiter('|'), Utf8),
        ["semicolon"] = Encode(WithDelimiter(';'), Utf8),

        // Quoting
        ["quoted-delimiter-quotes-newlines"] = Encode(
            "a,b,c\r\n\"x,y\",\"He said \"\"hi\"\"\",\"line1\r\nline2\"\r\n1,\"lf\nonly\",\"cr\ronly\"\r\n", Utf8),
        ["quoted-when-not-needed"] = Encode("\"a\",\"b\"\r\n\"1\",\"2\"\r\n", Utf8),
        ["quoted-empty-vs-unquoted-empty"] = Encode("a,b,c\r\n,\"\",\r\n\"\",,\"\"\r\n", Utf8),
        ["stray-quote-in-unquoted-field"] = Encode("a,b\r\n5\" pipe,x\r\n", Utf8),
        ["spaces-around-values"] = Encode("a,b\r\n  x , y \r\n", Utf8),
        ["leading-zeros-stay-text"] = Encode("Zip,Id\r\n02134,007\r\n", Utf8),

        // Line endings
        ["lf"] = Encode("a,b\n1,2\n3,4\n", Utf8),
        ["cr-only"] = Encode("a,b\r1,2\r", Utf8),
        ["mixed-line-endings"] = Encode("a,b\r\n1,2\n3,4\r\n5,6\r", Utf8),
        ["no-trailing-newline"] = Encode("a,b\r\n1,2", Utf8),
        ["header-only"] = Encode("a,b\r\n", Utf8),
        ["header-only-no-newline"] = Encode("a,b", Utf8),
        ["blank-lines"] = Encode("a,b\r\n1,2\r\n\r\n3,4\r\n\r\n", Utf8),
        ["single-column-with-empty-row"] = Encode("Name\r\nA\r\n\r\nB\r\n", Utf8),

        // Headers
        ["duplicate-headers"] = Encode("Name,Name,Id,Name\r\na,b,1,c\r\n", Utf8),
        ["blank-headers"] = Encode("Id,,Name, \r\n1,2,3,4\r\n", Utf8),
        ["quoted-header-with-delimiter"] = Encode("\"Last, First\",Id\r\n\"Doe, J\",1\r\n", Utf8),

        // Combinations
        ["utf16le-bom-tab-quoted-newlines"] = Encode("Id\tNote\r\n1\t\"multi\r\nline\tcell\"\r\n2\tZoë\r\n", Utf16LeBom),
        ["1252-semicolon-quoted"] = Encode("Id;Price;Note\r\n1;€10,50;\"a;b “q”\"\r\n", Win1252),
        ["utf8-bom-pipe-lf"] = Encode("Id|Name\n1|\"A|B\"\n2|José\n", Utf8Bom),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Round_trip_is_byte_identical(string caseName)
    {
        byte[] input = Cases[caseName];

        byte[] output = RoundTrip(input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Windows1252_bytes_survive_including_undefined_positions()
    {
        // 0x80 = €, 0x93/0x94 = curly quotes; 0x81/0x8D/0x8F/0x90/0x9D are unassigned in 1252.
        byte[] input = [.. "a,b\r\n"u8, 0x80, 0x93, 0x94, (byte)',', 0x81, 0x8D, 0x8F, 0x90, 0x9D, .. "\r\n"u8];

        Assert.Equal(input, RoundTrip(input));
    }
}
