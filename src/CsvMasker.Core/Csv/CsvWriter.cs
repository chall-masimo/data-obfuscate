using System.Buffers;
using System.Text;

namespace CsvMasker.Core.Csv;

/// <summary>
/// Streaming CSV writer. Writes the dialect's BOM, then one record at a time, encoding each record
/// as it is written so an unencodable character is reported against the right record.
/// </summary>
public sealed class CsvWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly Encoding _encoding;
    private readonly string _encodingName;
    private readonly char _delimiter;
    private readonly string _defaultLineEnding;
    private readonly bool _leaveOpen;
    private readonly SearchValues<char> _sourceQuoteTriggers;
    private readonly SearchValues<char> _newQuoteTriggers;
    private char[] _chars = new char[4096];
    private int _charCount;
    private byte[] _bytes = [];
    private long _recordNumber;
    private bool _finalRecordWritten;
    private bool _disposed;

    public CsvWriter(Stream stream, CsvDialect dialect, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(dialect);
        if (dialect.Delimiter is '"' or '\r' or '\n')
            throw new ArgumentException("The delimiter cannot be a quote or a line break.", nameof(dialect));
        if (!IsValidLineEnding(dialect.LineEnding) || dialect.LineEnding.Length == 0)
            throw new ArgumentException("The dialect line ending must be \\r\\n, \\n or \\r.", nameof(dialect));

        _stream = stream;
        _encoding = dialect.Encoding.Encoding;
        _encodingName = dialect.Encoding.Name;
        _delimiter = dialect.Delimiter;
        _defaultLineEnding = dialect.LineEnding;
        _leaveOpen = leaveOpen;
        _sourceQuoteTriggers = SearchValues.Create([_delimiter, '\r', '\n']);
        _newQuoteTriggers = SearchValues.Create([_delimiter, '\r', '\n', '"']);

        _stream.Write(dialect.Encoding.Preamble);
    }

    public long RecordsWritten => _recordNumber;

    /// <summary>Writes a record with its source quoting and line ending.</summary>
    public void WriteRecord(CsvRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        WriteRecord(record.Values, record.WasQuoted, record.LineEnding);
    }

    /// <summary>
    /// Writes one record.
    /// </summary>
    /// <param name="values">Field values.</param>
    /// <param name="wasQuoted">
    /// Source quoting per field. A field that was quoted stays quoted. A field that wasn't is
    /// quoted only if it now contains the delimiter or a line break, or starts with a quote; a
    /// quote elsewhere in it is left bare, matching how the reader accepted it. When null (values
    /// with no source), fields are quoted by strict RFC 4180 rules.
    /// </param>
    /// <param name="lineEnding">Terminator; null means the dialect default, "" means none (final record only).</param>
    /// <exception cref="CsvFormatException">A value can't be represented in the output encoding.</exception>
    public void WriteRecord(IReadOnlyList<string> values, IReadOnlyList<bool>? wasQuoted = null, string? lineEnding = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);
        if (wasQuoted is not null && wasQuoted.Count != values.Count)
            throw new ArgumentException("wasQuoted must have one entry per value.", nameof(wasQuoted));
        if (lineEnding is not null && !IsValidLineEnding(lineEnding))
            throw new ArgumentException("The line ending must be \\r\\n, \\n, \\r or empty.", nameof(lineEnding));
        if (_finalRecordWritten)
            throw new InvalidOperationException("A record without a line ending must be the last record written.");

        _recordNumber++;
        _charCount = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                Append(_delimiter);
            AppendField(values[i] ?? "", wasQuoted?[i]);
        }

        lineEnding ??= _defaultLineEnding;
        Append(lineEnding);
        _finalRecordWritten = lineEnding.Length == 0;

        EncodeAndWrite();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Flush();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private static bool IsValidLineEnding(string lineEnding) => lineEnding is "\r\n" or "\n" or "\r" or "";

    private void AppendField(string value, bool? wasQuoted)
    {
        bool quote = wasQuoted switch
        {
            true => true,
            false => value.StartsWith('"') || value.AsSpan().ContainsAny(_sourceQuoteTriggers),
            null => value.AsSpan().ContainsAny(_newQuoteTriggers),
        };

        if (!quote)
        {
            Append(value);
            return;
        }

        Append('"');
        foreach (char c in value)
        {
            if (c == '"')
                Append('"');
            Append(c);
        }
        Append('"');
    }

    private void Append(char c)
    {
        EnsureCapacity(1);
        _chars[_charCount++] = c;
    }

    private void Append(string s)
    {
        EnsureCapacity(s.Length);
        s.CopyTo(_chars.AsSpan(_charCount));
        _charCount += s.Length;
    }

    private void EnsureCapacity(int extra)
    {
        if (_charCount + extra > _chars.Length)
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _charCount + extra));
    }

    private void EncodeAndWrite()
    {
        int maxBytes = _encoding.GetMaxByteCount(_charCount);
        if (_bytes.Length < maxBytes)
            _bytes = new byte[Math.Max(maxBytes, _bytes.Length * 2)];

        int byteCount;
        try
        {
            byteCount = _encoding.GetBytes(_chars, 0, _charCount, _bytes, 0);
        }
        catch (EncoderFallbackException)
        {
            throw CsvFormatException.UnencodableOutput(_recordNumber, _encodingName);
        }

        _stream.Write(_bytes, 0, byteCount);
    }
}
