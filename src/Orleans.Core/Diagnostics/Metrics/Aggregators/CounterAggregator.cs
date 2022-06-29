using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Runtime;

internal class CounterAggregatorGroup
{
    private readonly Dictionary<string, CounterAggregator> Aggregators = new();

    public CounterAggregator FindOrCreate(params KeyValuePair<string, object>[] tags)
    {
        // TODO: better to use a hash of the tags?
        // or a struct record: TagGroupKey(string? tagName1, object? tagValue1, string? tagName2, object? tagValue2, ...)
        var key = string.Join("&", tags.Select(t => t.Key + "=" + t.Value));
        ref var aggregator = ref CollectionsMarshal.GetValueRefOrAddDefault(Aggregators, key, out var exists);
        if (!exists)
        {
            aggregator = new CounterAggregator(tags);
        }
        return aggregator;
    }

    public IEnumerable<Measurement<long>> Collect() => Aggregators.Values.Select(c => c.Collect());
}

internal class CounterAggregator
{
    private static readonly Dictionary<AggregatorKey, CounterAggregator> Aggregators = new();

    private static CounterAggregator FindOrCreate(string name, params KeyValuePair<string, object>[] tags)
    {
        var key = new AggregatorKey(name, tags);
        ref var aggregator = ref CollectionsMarshal.GetValueRefOrAddDefault(Aggregators, key, out var exists);
        if (!exists)
        {
            aggregator = new CounterAggregator(tags);
        }
        return aggregator;
    }

    private long _delta = 0;
    private KeyValuePair<string, object>[] tags;

    public CounterAggregator() : this(Array.Empty<KeyValuePair<string, object>>())
    { }

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
