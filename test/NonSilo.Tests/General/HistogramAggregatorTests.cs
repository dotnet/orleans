using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.General;

public class HistogramAggregatorTests
{
    [Fact, TestCategory("Functional"), TestCategory("Aggregators")]
    public void CollectBuckets()
    {
        var bounds = new long[] { 1, 3, 5, 8, 13 };
        var aggregator = new HistogramAggregator(bounds, new[] { new KeyValuePair<string, object>("foo", "bar") }, bound => new("le", bound));
        aggregator.Record(0);
        aggregator.Record(2);
        aggregator.Record(5);
        aggregator.Record(7);
        aggregator.Record(10);
        aggregator.Record(11);
        aggregator.Record(13);
        aggregator.Record(15);
        aggregator.Record(20);
        aggregator.Record(100);
        var buckets = aggregator.CollectBuckets().ToArray();
        for(int i = 0; i < bounds.Length; i++)
        {
            Assert.Equal("foo", buckets[i].Tags[0].Key);
            Assert.Equal("bar", buckets[i].Tags[0].Value);
            Assert.Equal("le", buckets[0].Tags[^1].Key);
            Assert.Equal(bounds[i], buckets[i].Tags[^1].Value);
        }
        Assert.Equal(long.MaxValue, buckets.Last().Tags[^1].Value);
        Assert.Equal(1, buckets[0].Value);
        Assert.Equal(1, buckets[1].Value);
        Assert.Equal(1, buckets[2].Value);
        Assert.Equal(1, buckets[3].Value);
        Assert.Equal(3, buckets[4].Value);
        Assert.Equal(3, buckets[5].Value);
    }
}