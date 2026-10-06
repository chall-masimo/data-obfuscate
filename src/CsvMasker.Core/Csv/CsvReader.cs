using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CsvMasker.Core.Csv;

/// <summary>
/// Forward-only, streaming RFC 4180 reader. Holds one record in memory at a time and records
/// everything <see cref="CsvWriter"/> needs to reproduce the input byte-for-byte: per-field quoting
/// and per-record line endings.
/// </summary>
/// <remarks>
/// Deliberate leniency: a quote inside an unquoted field (<c>5" pipe</c>) is kept literally, as
/// Excel and most database exports expect. Text after a closing quote (<c>"a"b</c>) is an error.
/// </remarks>
public sealed class CsvReader : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TextReader _reader;
    private readonly char _delimiter;
    private readonly string _encodingName;
    private readonly int _maxRecordChars;
    private readonly bool _validateFieldCount;
    private readonly SearchValues<char> _unquotedStops;
    private readonly char[] _buffer = new char[BufferSize];
    private readonly StringBuilder _field = new();
    private readonly List<string> _values = [];
    private readonly List<bool> _quoted = [];
    private int _pos;
    private int _len;
    private bool _eof;
    private long _recordNumber;
    private int _recordChars;
    private int _headerCount;

    /// <summary>
    /// Opens <paramref name="stream"/> (positioned at the start of the file, BOM included) and reads
    /// the header row.
    /// </summary>
    /// <exception cref="CsvFormatException">The file is empty or the header is malformed.</exception>
    public CsvReader(Stream stream, CsvDialect dialect, CsvReaderOptions? options = null, bool leaveOpen = false)
        : this(OpenText(stream, dialect, leaveOpen), dialect.Delimiter, dialect.Encoding.Name, options, validateFieldCount: true)
    {
    }

    internal CsvReader(TextReader reader, char delimiter, string encodingName, CsvReaderOptions? options, bool validateFieldCount)
    {
        _reader = reader;
        try
        {
            if (delimiter is '"' or '\r' or '\n')
                throw new ArgumentException("The delimiter cannot be a quote or a line break.", nameof(delimiter));

            _delimiter = delimiter;
            _encodingName = encodingName;
            _maxRecordChars = (options ?? CsvReaderOptions.Default).MaxRecordChars;
            _validateFieldCount = validateFieldCount;
            _unquotedStops = SearchValues.Create([delimiter, '\r', '\n']);

            var header = ReadRecord() ?? throw CsvFormatException.MissingHeader();
            Header = new CsvHeader(header);
            _headerCount = Header.Count;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public CsvHeader Header { get; }

    /// <summary>
    /// Reads the next data record.
    /// </summary>
    /// <exception cref="CsvFormatException">
    /// The record is malformed. After <see cref="CsvErrorKind.FieldCountMismatch"/> the reader is
    /// positioned at the next record and reading can continue; after any other kind it cannot.
    /// </exception>
    public bool TryRead([NotNullWhen(true)] out CsvRecord? record)
    {
        record = ReadRecord();
        if (record is null)
            return false;

        if (_validateFieldCount && !record.IsBlankLine && record.Values.Count != _headerCount)
            throw CsvFormatException.FieldCountMismatch(record.RecordNumber, _headerCount, record.Values.Count);

        return true;
    }

    public void Dispose() => _reader.Dispose();

    private static StreamReader OpenText(Stream stream, CsvDialect dialect, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(dialect);

        // The encodings carry no preamble of their own (so StreamReader won't strip or expect one);
        // the BOM is consumed here and written back by CsvWriter.
        var expected = dialect.Encoding.Preamble;
        if (expected.Length > 0)
        {
            Span<byte> actual = stackalloc byte[expected.Length];
            int read = stream.ReadAtLeast(actual, actual.Length, throwOnEndOfStream: false);
            if (!actual[..read].SequenceEqual(expected))
                throw new InvalidDataException("The stream does not start with the byte order mark the dialect expects.");
        }

        return new StreamReader(stream, dialect.Encoding.Encoding, detectEncodingFromByteOrderMarks: false, BufferSize, leaveOpen);
    }

    private CsvRecord? ReadRecord()
    {
        if (Peek() < 0)
            return null;

        _recordNumber++;
        _recordChars = 0;
        _values.Clear();
        _quoted.Clear();

        string lineEnding;
        while (true)
        {
            bool quoted = Peek() == '"';
            if (quoted)
                ReadQuotedField();
            else
                ReadUnquotedField();

            _values.Add(_field.ToString());
            _quoted.Add(quoted);

            int c = Read();
            if (c == _delimiter)
                continue;

            lineEnding = c switch
            {
                -1 => "",
                '\n' => "\n",
                _ => ReadAfterCarriageReturn(),
            };
            break;
        }

        bool isBlankLine = _headerCount > 1 && _values.Count == 1 && !_quoted[0] && _values[0].Length == 0;
        return new CsvRecord(_values.ToArray(), _quoted.ToArray(), lineEnding, _recordNumber, isBlankLine);
    }

    private string ReadAfterCarriageReturn()
    {
        if (Peek() != '\n')
            return "\r";
        _pos++;
        return "\r\n";
    }

    private void ReadUnquotedField()
    {
        _field.Clear();
        while (_pos < _len || Fill())
        {
            var span = _buffer.AsSpan(_pos, _len - _pos);
            int stop = span.IndexOfAny(_unquotedStops);
            Append(stop < 0 ? span : span[..stop]);
            if (stop >= 0)
            {
                _pos += stop;
                return;
            }
            _pos = _len;
        }
    }

    private void ReadQuotedField()
    {
        _field.Clear();
        int fieldNumber = _values.Count + 1;
        _pos++; // opening quote

        while (true)
        {
            if (_pos >= _len && !Fill())
                throw CsvFormatException.UnclosedQuote(_recordNumber, fieldNumber);

            var span = _buffer.AsSpan(_pos, _len - _pos);
            int quote = span.IndexOf('"');
            if (quote < 0)
            {
                Append(span);
                _pos = _len;
                continue;
            }

            Append(span[..quote]);
            _pos += quote + 1;
            if (Peek() != '"')
                break;

            // Doubled quote: an escaped literal quote.
            Append("\"");
            _pos++;
        }

        int next = Peek();
        if (next >= 0 && next != _delimiter && next != '\r' && next != '\n')
            throw CsvFormatException.TextAfterClosingQuote(_recordNumber, fieldNumber);
    }

    private void Append(ReadOnlySpan<char> chars)
    {
        _recordChars += chars.Length;
        if (_recordChars > _maxRecordChars)
            throw CsvFormatException.RecordTooLarge(_recordNumber, _maxRecordChars);
        _field.Append(chars);
    }

    private int Peek() => _pos < _len || Fill() ? _buffer[_pos] : -1;

    private int Read() => _pos < _len || Fill() ? _buffer[_pos++] : -1;

    private bool Fill()
    {
        if (_eof)
            return false;

        try
        {
            _len = _reader.Read(_buffer, 0, _buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            // The decoder reads ahead, so the bad bytes are somewhere at or after this record.
            throw CsvFormatException.UndecodableInput(Math.Max(_recordNumber, 1), _encodingName);
        }

        _pos = 0;
        _eof = _len == 0;
        return !_eof;
    }
}
