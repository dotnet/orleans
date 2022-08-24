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
        foreach (var bucket in _buckets)
        {
            yield return bucket.Collect();
        }
    }

    public Measurement<long> CollectCount() => new(_count, _tags);

    public Measurement<long> CollectSum() => new(_sum, _tags);
}
