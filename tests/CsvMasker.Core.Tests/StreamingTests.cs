using System.Text;
using CsvMasker.Core.Csv;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests;

public class StreamingTests
{
    [Fact]
    public void Reads_lazily_from_a_non_seekable_stream()
    {
        const int rows = 200_000;
        using var source = new GeneratedCsvStream(rows);
        using var reader = new CsvReader(source, Dialect(Utf8));

        Assert.True(reader.TryRead(out var first));
        Assert.Equal(["1", "Name 1"], first.Values);
        // Only the read-ahead buffers have been pulled, not the multi-MB file.
        Assert.True(source.BytesProduced < 512 * 1024, $"Produced {source.BytesProduced:N0} bytes for one record.");

        long count = 1;
        CsvRecord last = first;
        while (reader.TryRead(out var record))
        {
            count++;
            last = record;
        }

        Assert.Equal(rows, count);
        Assert.Equal(rows + 1, last.RecordNumber);
        Assert.Equal([rows.ToString(), $"Name {rows}"], last.Values);
    }

    /// <summary>Produces "Id,Name" plus <c>rows</c> data lines on demand, without ever holding the file.</summary>
    private sealed class GeneratedCsvStream(int rows) : Stream
    {
        private byte[] _pending = "Id,Name\r\n"u8.ToArray();
        private int _pendingPos;
        private int _nextRow = 1;

        public long BytesProduced { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_pendingPos == _pending.Length)
                {
                    if (_nextRow > rows)
                        break;
                    _pending = Encoding.UTF8.GetBytes($"{_nextRow},Name {_nextRow}\r\n");
                    _pendingPos = 0;
                    _nextRow++;
                }

                int n = Math.Min(count - written, _pending.Length - _pendingPos);
                Array.Copy(_pending, _pendingPos, buffer, offset + written, n);
                _pendingPos += n;
                written += n;
            }

            BytesProduced += written;
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Wraps a stream and hides its seekability.</summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
