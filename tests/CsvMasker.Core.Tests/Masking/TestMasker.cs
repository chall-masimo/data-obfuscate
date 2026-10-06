using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Verification;

namespace CsvMasker.Core.Tests.Masking;

/// <summary>Masks single values through the real ColumnMasker (invariants included) with a fixed key.</summary>
internal sealed class TestMasker
{
    public static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public static readonly byte[] KeyB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    private readonly ColumnMasker _masker;
    private readonly ColumnVerifier _verifier;
    private long _recordNumber = 1;

    public TestMasker(ColumnRule rule, byte[]? key = null, MaskingOptions? options = null, string column = "Col")
    {
        options ??= MaskingOptions.Default;
        string domain = rule.MappingDomain ?? column;
        var strategy = MaskingPipeline.CreateStrategy(rule, domain, new SeedSource((byte[])(key ?? KeyA).Clone()), options);
        _verifier = new ColumnVerifier(column, rule.Strategy, strategy.IsMapping) { Warning = strategy.Warning };
        _masker = new ColumnMasker(column, strategy, domain, new MappingStore(options.MaxMappingEntries), _verifier);
    }

    public TestMasker(MaskingStrategy strategy, StrategyOptions? options = null, byte[]? key = null)
        : this(new ColumnRule(strategy, options), key)
    {
    }

    /// <summary>Masks one value as the next data row (record numbers count up from 2).</summary>
    public string Mask(string value, bool isNull = false)
    {
        var row = new CsvRecord([value], [false], "\r\n", ++_recordNumber);
        string masked = _masker.Mask(value, isNull, row);
        _verifier.Record(value, isNull, masked, isNull && masked.Length == 0);
        return masked;
    }

    public ColumnVerification Verification => _verifier.Build();
}
