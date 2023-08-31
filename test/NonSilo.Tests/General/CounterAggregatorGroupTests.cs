using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General;

public class CounterAggregatorGroupTests
{
    private readonly ITestOutputHelper _output;
    public CounterAggregatorGroupTests(ITestOutputHelper output)
    {
        this._output = output;
    }

    [Fact, TestCategory("Functional"), TestCategory("Aggregators")]
    public void ValidateAggregatorCache()
    {
        var group = new CounterAggregatorGroup();

        var aggregator1 = group.FindOrCreate(new("foo", "bar"));
        var aggregator2 = group.FindOrCreate(new("foo", "bar"));

        Assert.Same(aggregator1, aggregator2);
        Assert.Single(group.Aggregators);
    }

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
