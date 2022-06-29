using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal class CounterAggregator
{
    private long _delta = 0;
    private readonly KeyValuePair<string, object>[] _tags;
    public CounterAggregator() : this(Array.Empty<KeyValuePair<string, object>>())
    { }

    public CounterAggregator(TagList tagList)
    {
        if (tagList.Name1 == null)
        {
            _tags = Array.Empty<KeyValuePair<string, object>>();
        }
        else if (tagList.Name2 == null)
        {
            _tags = new[] { new KeyValuePair<string, object>(tagList.Name1, tagList.Value1) };
        }
        else if (tagList.Name3 == null)
        {
            _tags = new[]
            {
                new KeyValuePair<string, object>(tagList.Name1, tagList.Value1),
                new KeyValuePair<string, object>(tagList.Name2, tagList.Value2)
            };
        }
        else if (tagList.Name4 == null)
        {
            _tags = new[]
            {
                new KeyValuePair<string, object>(tagList.Name1, tagList.Value1),
                new KeyValuePair<string, object>(tagList.Name2, tagList.Value2),
                new KeyValuePair<string, object>(tagList.Name3, tagList.Value3)
            };
        }
        else
        {
            _tags = new[]
            {
                new KeyValuePair<string, object>(tagList.Name1, tagList.Value1),
                new KeyValuePair<string, object>(tagList.Name2, tagList.Value2),
                new KeyValuePair<string, object>(tagList.Name3, tagList.Value3),
                new KeyValuePair<string, object>(tagList.Name4, tagList.Value4)
            };
        }
    }

    public CounterAggregator(KeyValuePair<string, object>[] tags)
    {
        _tags = tags;
    }

    public void Add(long measurement) => Interlocked.Add(ref _delta, measurement);

    public Measurement<long> Collect()
    {
        var sum = Interlocked.Exchange(ref _delta, 0);
        return new Measurement<long>(sum, _tags);
    }
}
