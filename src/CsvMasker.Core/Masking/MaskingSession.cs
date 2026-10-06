using System.Security.Cryptography;
using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Masking.Verification;

namespace CsvMasker.Core.Masking;

public sealed record MaskingOptions
{
    public static MaskingOptions Default { get; } = new();

    /// <summary>Total source→output entries across all mapping domains (<c>Limits:MaxMappingEntries</c>).</summary>
    public long MaxMappingEntries { get; init; } = 2_000_000;

    /// <summary>Stop at the first malformed row (default), or skip it: it is left out of the output and reported.</summary>
    public bool FailOnMalformed { get; init; } = true;

    public int MaxMalformedRowsListed { get; init; } = 100;

    /// <summary>How often (in data rows) progress is reported.</summary>
    public int ProgressInterval { get; init; } = 10_000;

    public IZipReference ZipReference { get; init; } = FallbackZipReference.Instance;

    public CsvReaderOptions Reader { get; init; } = CsvReaderOptions.Default;
}

/// <summary>An original row and its masked version, for the preview screen. Holds real data: never persist or log.</summary>
public sealed record PreviewRow(long RecordNumber, IReadOnlyList<string> Original, IReadOnlyList<string> Masked);

/// <summary>
/// One masking job. Generates a fresh 32-byte key held only in memory, used for the preview and
/// the run so both agree, and zeroed on <see cref="Dispose"/>. There is no way to read the key
/// out, and nothing here can reverse a mapping.
/// </summary>
public sealed class MaskingSession : IDisposable
{
    private readonly byte[] _key;
    private readonly MaskingOptions _options;
    private bool _disposed;

    public MaskingSession(MaskingOptions? options = null)
        : this(RandomNumberGenerator.GetBytes(32), options)
    {
    }

    /// <summary>For tests that need repeatable output.</summary>
    internal MaskingSession(byte[] key, MaskingOptions? options)
    {
        _key = key;
        _options = options ?? MaskingOptions.Default;
    }

    /// <summary>Masks the first <paramref name="rows"/> data rows. A seekable stream's dialect is detected.</summary>
    public IReadOnlyList<PreviewRow> Preview(Stream input, MaskingPlan plan, int rows = 20)
    {
        var dialect = CsvDialectDetector.Detect(input);
        return Preview(input, dialect, plan, rows);
    }

    public IReadOnlyList<PreviewRow> Preview(Stream input, CsvDialect dialect, MaskingPlan plan, int rows = 20)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var preview = new List<PreviewRow>(rows);
        MaskingPipeline.Run(input, dialect, Stream.Null, plan, _key, _options, null, CancellationToken.None,
            onRow: (original, masked) =>
            {
                preview.Add(new PreviewRow(original.RecordNumber, original.Values, masked.Values));
                return preview.Count < rows;
            });
        return preview;
    }

    /// <summary>Masks the whole file into <paramref name="output"/>. A seekable input's dialect is detected.</summary>
    /// <remarks>On failure the output is incomplete; the caller must delete it.</remarks>
    public VerificationReport Run(Stream input, Stream output, MaskingPlan plan, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var dialect = CsvDialectDetector.Detect(input);
        return Run(input, dialect, output, plan, progress, cancellationToken);
    }

    public VerificationReport Run(Stream input, CsvDialect dialect, Stream output, MaskingPlan plan, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return MaskingPipeline.Run(input, dialect, output, plan, _key, _options, progress, cancellationToken, onRow: null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}
