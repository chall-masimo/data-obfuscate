using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using Xunit.Abstractions;

namespace CsvMasker.Core.Tests.Masking;

public class MemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void Two_million_rows_stream_with_bounded_memory()
    {
        const int rows = 2_000_000;
        var regions = new[] { "North", "South", "East", "West", "Central" };
        var start = new DateTime(2020, 1, 1);
        using var source = new GeneratedStream("Id,Region,Amount,Order_Date,Notes\r\n",
            i => $"{i},{regions[i % 5]},{i % 9973 * 1.25:F2},{start.AddDays(i % 1500):yyyy-MM-dd},note {i}\r\n", rows);

        var dialect = new CsvDialect(CsvEncodingInfo.Utf8(withBom: false), ',', "\r\n");
        var plan = new MaskingPlan(new Dictionary<string, ColumnRule>
        {
            ["Id"] = new(MaskingStrategy.Keep),
            ["Region"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.City)), // low-cardinality mapping column
            ["Amount"] = new(MaskingStrategy.Perturb),
            ["Order_Date"] = new(MaskingStrategy.DateShift, new DateShiftOptions(Formats: ["yyyy-MM-dd"])),
            ["Notes"] = new(MaskingStrategy.Redact),
        });

        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        var sampler = new MemorySampler();
        var stopwatch = Stopwatch.StartNew();

        using var session = new MaskingSession(new MaskingOptions { ProgressInterval = 200_000 });
        var report = session.Run(source, dialect, Stream.Null, plan, sampler);

        stopwatch.Stop();
        long peakMb = (sampler.Peak - baseline) / (1024 * 1024);
        output.WriteLine($"{rows:N0} rows in {stopwatch.Elapsed.TotalSeconds:F1}s, peak managed memory above baseline: {peakMb} MB");

        Assert.Equal(rows, report.RowsWritten);
        Assert.False(report.HasFailures);
        Assert.True(peakMb < 50, $"Peak managed memory grew by {peakMb} MB");
    }

    private sealed class MemorySampler : IProgress<long>
    {
        public long Peak { get; private set; }

        public void Report(long value) => Peak = Math.Max(Peak, GC.GetTotalMemory(forceFullCollection: true));
    }

    /// <summary>Non-seekable stream that produces a header plus generated lines on demand.</summary>
    private sealed class GeneratedStream(string header, Func<int, string> line, int rows) : Stream
    {
        private byte[] _pending = Encoding.UTF8.GetBytes(header);
        private int _pendingPos;
        private int _next;

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
                    if (_next == rows) break;
                    _pending = Encoding.UTF8.GetBytes(line(_next++));
                    _pendingPos = 0;
                }
                int n = Math.Min(count - written, _pending.Length - _pendingPos);
                Array.Copy(_pending, _pendingPos, buffer, offset + written, n);
                _pendingPos += n;
                written += n;
            }
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
