using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime;

internal class HistogramAggregator
{
    private readonly KeyValuePair<string, object>[] _tags;
    private readonly HistogramBucketAggregator[] _buckets;
    private long _count;
    private long _sum;

    public HistogramAggregator(long[] buckets, KeyValuePair<string, object>[] tags, Func<long, KeyValuePair<string, object>> getLabel)
    {
        if (buckets[^1] != long.MaxValue)
        {
            buckets = buckets.Concat(new[] { long.MaxValue }).ToArray();
        }

        _tags = tags;
        _buckets = buckets.Select(b => new HistogramBucketAggregator(tags, b, getLabel(b))).ToArray();
    }

    public void Record(long number)
    {
        int i;
        for (i = 0; i < _buckets.Length; i++)
        {
            if (number <= _buckets[i].Bound)
            {
                break;
            }
        }
        _buckets[i].Add(1);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sum, number);
    }

    public IEnumerable<Measurement<long>> CollectBuckets()
    {
        long count = 0;
        foreach (var bucket in _buckets)
        {
            count += bucket.Value;
            yield return new Measurement<long>(count, bucket.Tags);
        }
    }

    public Measurement<long> CollectCount()
    {
        return new Measurement<long>(_count, _tags);
    }

    public Measurement<long> CollectSum()
    {
        return new Measurement<long>(_sum, _tags);
    }
}
