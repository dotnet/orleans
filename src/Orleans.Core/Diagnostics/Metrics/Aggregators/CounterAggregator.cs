using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime;

internal class CounterAggregatorGroup
{
    private readonly ConcurrentDictionary<TagList, CounterAggregator> _aggregators = new();
    internal ConcurrentDictionary<TagList, CounterAggregator> Aggregators => _aggregators;
    public CounterAggregator FindOrCreate(string tagName1, object tagValue1) => FindOrCreate(new TagList(tagName1, tagValue1));

    public CounterAggregator FindOrCreate(string tagName1, object tagValue1, string tagName2, object tagValue2) => FindOrCreate(new TagList(tagName1, tagValue1, tagName2, tagValue2));

    public CounterAggregator FindOrCreate(TagList tagGroup)
    {
        if (Aggregators.TryGetValue(tagGroup, out var stat))
        {
            return stat;
        }
        return Aggregators.GetOrAdd(tagGroup, new CounterAggregator(tagGroup));
    }

    public IEnumerable<Measurement<long>> Collect() => _aggregators.Values.Select(c => c.Collect());
}

internal class CounterAggregator
{
    private long _delta = 0;
    private KeyValuePair<string, object>[] tags;
    public CounterAggregator() : this(Array.Empty<KeyValuePair<string, object>>())
    { }

    public CounterAggregator(TagList tagGroup)
    {
        if (tagGroup.Name1 == null)
        {
            tags = Array.Empty<KeyValuePair<string, object>>();
        }
        else if (tagGroup.Name2 == null)
        {
            tags = new[] { new KeyValuePair<string, object>(tagGroup.Name1, tagGroup.Value1) };
        }
        else if (tagGroup.Name3 == null)
        {
            tags = new[]
            {
                new KeyValuePair<string, object>(tagGroup.Name1, tagGroup.Value1),
                new KeyValuePair<string, object>(tagGroup.Name2, tagGroup.Value2)
            };
        }
        else if (tagGroup.Name4 == null)
        {
            tags = new[]
            {
                new KeyValuePair<string, object>(tagGroup.Name1, tagGroup.Value1),
                new KeyValuePair<string, object>(tagGroup.Name2, tagGroup.Value2),
                new KeyValuePair<string, object>(tagGroup.Name3, tagGroup.Value3)
            };
        }
        else
        {
            tags = new[]
            {
                new KeyValuePair<string, object>(tagGroup.Name1, tagGroup.Value1),
                new KeyValuePair<string, object>(tagGroup.Name2, tagGroup.Value2),
                new KeyValuePair<string, object>(tagGroup.Name3, tagGroup.Value3),
                new KeyValuePair<string, object>(tagGroup.Name4, tagGroup.Value4)
            };
        }
    }

    public CounterAggregator(KeyValuePair<string, object>[] tags)
    {
        this.tags = tags;
    }

    public void Add(long measurement) => Interlocked.Add(ref _delta, measurement);

    public Measurement<long> Collect()
    {
        var sum = Interlocked.Exchange(ref _delta, 0);
        return new Measurement<long>(sum, tags);
    }
}
