using System.Text;

namespace CsvMasker.Core.Csv;

/// <summary>
/// Detects encoding, delimiter and line ending. The delimiter comes from the header line; data
/// records are only consulted to break a tie.
/// </summary>
public static class CsvDialectDetector
{
    // Order doubles as the tie-break preference.
    private static readonly char[] Candidates = [',', '\t', ';', '|'];
    private const int SampleChars = 256 * 1024;
    private const int ConfirmationRecords = 20;

    /// <summary>
    /// Detects the dialect of <paramref name="stream"/>, which must be seekable. The stream is
    /// returned to its original position afterwards.
    /// </summary>
    public static CsvDialect Detect(Stream stream)
    {
        var encoding = CsvEncodingDetector.Detect(stream);
        string sample = ReadSample(stream, encoding);
        var (headerLine, lineEnding) = SplitHeaderLine(sample);
        char delimiter = ChooseDelimiter(headerLine, sample, encoding.Name);

        // A header with no terminator means a one-line file; CRLF is then only the default for
        // records added later, and the header itself is still written back without one.
        return new CsvDialect(encoding, delimiter, lineEnding.Length == 0 ? "\r\n" : lineEnding);
    }

    private static string ReadSample(Stream stream, CsvEncodingInfo encoding)
    {
        long start = stream.Position;
        try
        {
            stream.Position = start + encoding.Preamble.Length;
            using var reader = new StreamReader(stream, encoding.Encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var buffer = new char[SampleChars];
            int total = 0, read;
            while (total < buffer.Length && (read = reader.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            return new string(buffer, 0, total);
        }
        catch (DecoderFallbackException)
        {
            throw CsvFormatException.UndecodableInput(1, encoding.Name);
        }
        finally
        {
            stream.Position = start;
        }
    }

    /// <summary>The first logical line (a line break inside quotes doesn't end it) and its terminator.</summary>
    private static (string Line, string LineEnding) SplitHeaderLine(string sample)
    {
        bool inQuotes = false;
        for (int i = 0; i < sample.Length; i++)
        {
            char c = sample[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c is '\r' or '\n')
            {
                string ending = c == '\n' ? "\n"
                    : i + 1 < sample.Length && sample[i + 1] == '\n' ? "\r\n"
                    : "\r";
                return (sample[..i], ending);
            }
        }
        return (sample, "");
    }

    private static char ChooseDelimiter(string headerLine, string sample, string encodingName)
    {
        var counts = Candidates.Select(c => CountOutsideQuotes(headerLine, c)).ToArray();
        int max = counts.Max();
        if (max == 0)
            return ','; // single column

        var tied = Candidates.Where((_, i) => counts[i] == max).ToArray();
        if (tied.Length == 1)
            return tied[0];

        char best = tied[0];
        int bestScore = -1;
        foreach (char candidate in tied)
        {
            int score = ConsistentRecordCount(sample, candidate, encodingName);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        int count = 0;
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (!inQuotes && c == delimiter)
                count++;
        }
        return count;
    }

    /// <summary>How many of the first data records have as many fields as the header under this delimiter.</summary>
    private static int ConsistentRecordCount(string sample, char delimiter, string encodingName)
    {
        int consistent = 0;
        try
        {
            using var reader = new CsvReader(
                new StringReader(sample), delimiter, encodingName,
                new CsvReaderOptions { MaxRecordChars = SampleChars }, validateFieldCount: false);

            for (int i = 0; i < ConfirmationRecords && reader.TryRead(out var record); i++)
            {
                if (!record.IsBlankLine && record.Values.Count == reader.Header.Count)
                    consistent++;
            }
        }
        catch (CsvFormatException)
        {
            // The sample may end mid-record, or this delimiter may simply be wrong; keep the count so far.
        }
        return consistent;
    }
}
