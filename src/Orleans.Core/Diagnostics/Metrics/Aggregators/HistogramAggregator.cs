using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Runtime;

internal class HistogramAggregator
{
    private readonly KeyValuePair<string, object>[] _tags;
    private readonly HistogramBucketAggregator[] _buckets;
    private long _count;
    private long _sum;

    public HistogramAggregator(long[] buckets, KeyValuePair<string, object>[] tags)
    {
        if (buckets[^1] != long.MaxValue)
        {
            buckets = buckets.Concat(new[] { long.MaxValue }).ToArray();
        }

        _tags = tags;
        _buckets = buckets.Select(b => new HistogramBucketAggregator(tags, b)).ToArray();
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
            count += bucket.Collect().Value;
            var tags = new KeyValuePair<string, object>[_tags.Length + 1];
            _tags.CopyTo(tags, 0);
            tags[^1] = new KeyValuePair<string, object>("le", bucket.Bound);
            yield return new Measurement<long>(count, tags);
        }
    }

    public Measurement<long> CollectCount()
    {
        var count = Interlocked.Exchange(ref _count, 0);
        return new Measurement<long>(count, _tags);
    }

    public Measurement<long> CollectSum()
    {
        var sum = Interlocked.Exchange(ref _sum, 0);
        return new Measurement<long>(sum, _tags);
    }

}
