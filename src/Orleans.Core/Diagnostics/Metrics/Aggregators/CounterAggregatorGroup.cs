using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class CounterAggregatorGroup
{
    internal ConcurrentDictionary<TagList, CounterAggregator> Aggregators { get; } = new();

    public CounterAggregator FindOrCreate(TagList tagList)
    {
        if (Aggregators.TryGetValue(tagList, out var stat))
        {
            return stat;
        }
        return Aggregators.GetOrAdd(tagList, new CounterAggregator(tagList));
    }

    public void Add(long measurement, string tagName1, object tagValue1)
        => FindOrCreate(new(tagName1, tagValue1))
            .Add(measurement);
    public void Add(long measurement, string tagName1, object tagValue1, string tagName2, object tagValue2)
        => FindOrCreate(new(tagName1, tagValue1, tagName2, tagValue2))
            .Add(measurement);
    public void Add(long measurement, string tagName1, object tagValue1, string tagName2, object tagValue2, string tagName3, object tagValue3)
        => FindOrCreate(new(tagName1, tagValue1, tagName2, tagValue2, tagName3, tagValue3))
            .Add(measurement);
    public void Add(long measurement, string tagName1, object tagValue1, string tagName2, object tagValue2, string tagName3, object tagValue3, string tagName4, object tagValue4)
        => FindOrCreate(new(tagName1, tagValue1, tagName2, tagValue2, tagName3, tagValue3, tagName4, tagValue4))
            .Add(measurement);
    public void Add(long measurement, TagList tagList)
        => FindOrCreate(tagList)
            .Add(measurement);

    public IEnumerable<Measurement<long>> Collect()
    {
        foreach (var (_, aggregator) in Aggregators)
        {
            if (aggregator.Value != 0)
            {
                yield return aggregator.Collect();
            }
        }
    }
}
