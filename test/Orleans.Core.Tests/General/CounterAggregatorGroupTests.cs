using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General;

/// <summary>
/// Tests for the CounterAggregatorGroup, which is part of Orleans' instrumentation and metrics infrastructure.
/// This component aggregates counter values from multiple sources and is used throughout Orleans for collecting
/// performance metrics such as message counts, activation counts, and other operational statistics.
/// </summary>
public class CounterAggregatorGroupTests
{
    private readonly ITestOutputHelper _output;
    public CounterAggregatorGroupTests(ITestOutputHelper output)
    {
        this._output = output;
    }

    /// <summary>
    /// Verifies that the aggregator group caches aggregator instances to avoid creating duplicates
    /// for the same metric name and tags combination.
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("Aggregators")]
    public void ValidateAggregatorCache()
    {
        var group = new CounterAggregatorGroup();

        var aggregator1 = group.FindOrCreate(new("foo", "bar"));
        var aggregator2 = group.FindOrCreate(new("foo", "bar"));

        Assert.Same(aggregator1, aggregator2);
        Assert.Single(group.Aggregators);
    }

    /// <summary>
    /// Tests the collection mechanism that retrieves aggregated metric values from all counters in the group.
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("Aggregators")]
    public void Collect()
    {
        var group = new CounterAggregatorGroup();

        var aggregator1 = group.FindOrCreate(new("foo", "bar1"));
        var aggregator2 = group.FindOrCreate(new("foo", "bar2"));

        aggregator1.Add(1);
        aggregator1.Add(2);
        aggregator2.Add(2);
        aggregator2.Add(3);

        var measurements = group.Collect().ToList();
        Assert.Equal(2, measurements.Count);
        Assert.Equal(3, measurements.Single(m => m.Tags[0].Value is "bar1").Value);
        Assert.Equal(5, measurements.Single(m => m.Tags[0].Value is "bar2").Value);
    }

    /// <summary>
    /// Stress test to verify thread-safety of the counter aggregator under high concurrent load.
    /// This ensures metrics collection remains accurate in Orleans' multi-threaded runtime environment.
    /// </summary>
    [Fact, TestCategory("Functional"), TestCategory("Aggregators")]
    public void TestMultithreadedCorrectness()
    {
        int numOfIterations = 1000000;

        var group = new CounterAggregatorGroup();
        var counterCount = Environment.ProcessorCount;

        Parallel.For(0, Environment.ProcessorCount, j =>
        {
            for (int i = 0; i < counterCount; i++)
            {
                var aggregator = group.FindOrCreate(new("test", i));

                for (int k = 0; k < numOfIterations; k++)
                {
                    aggregator.Add(i);
                }
            }
        });

        var measurements = group.Collect().OrderBy(m => m.Tags[0].Value).ToList();
        foreach (var measurement in measurements)
        {
            var i = (int)measurement.Tags[0].Value;
            _output.WriteLine("{0} {1}", i, measurement.Value);
            Assert.Equal(i * Environment.ProcessorCount * numOfIterations, measurement.Value);
        }
    }

}
