using CsvMasker.Core.Profiling;

namespace CsvMasker.Core.Tests.Profiling;

public class DistinctCounterTests
{
    [Fact]
    public void Exact_up_to_the_limit()
    {
        var counter = new DistinctCounter(exactLimit: 1_000);
        for (int i = 0; i < 5_000; i++)
            counter.Add($"v{i % 1_000}");

        Assert.True(counter.IsExact);
        Assert.Equal(1_000, counter.Count);
        Assert.Equal(1_000, counter.ExactValues!.Count);
    }

    [Theory]
    [InlineData(1_001)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    [InlineData(80_000)]   // around the linear-counting / HyperLogLog switch-over
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public void Estimate_is_within_5_percent(int distinct)
    {
        var counter = new DistinctCounter(exactLimit: 1_000);
        for (int i = 0; i < distinct; i++)
        {
            counter.Add($"customer-{i}");
            if (i % 3 == 0)
                counter.Add($"customer-{i / 2}"); // repeats don't change the count
        }

        Assert.False(counter.IsExact);
        Assert.Null(counter.ExactValues);
        Assert.InRange(counter.Count, distinct * 0.95, distinct * 1.05);
    }

    [Fact]
    public void Profiling_memory_stays_bounded_on_wide_unique_data()
    {
        // 100 columns × 20,000 unique values. Exact counting would hold all 2M strings (>100 MB);
        // bounded counting holds ~1,000 strings + 16 KB of registers per column.
        const int columns = 100, rows = 20_000;
        long before = GC.GetTotalMemory(forceFullCollection: true);

        var accumulators = Enumerable.Range(0, columns)
            .Select(c => new ColumnAccumulator($"Col{c}", ProfileOptions.Default))
            .ToArray();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                accumulators[c].Add($"value-{c}-{r}-padding", isNull: false);

        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(accumulators);

        Assert.True(retained < 30 * 1024 * 1024, $"Retained {retained / 1024 / 1024} MB");
        Assert.All(accumulators, a => Assert.InRange(a.Distinct.Count, rows * 0.95, rows * 1.05));
    }
}
