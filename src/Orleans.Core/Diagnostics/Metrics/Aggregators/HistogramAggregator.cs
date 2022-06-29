using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Runtime;

internal class HistogramAggregator
{
    private static readonly Dictionary<AggregatorKey, HistogramAggregator> Aggregators = new();

    public static HistogramAggregator FindOrCreate(string name, long[] buckets, params KeyValuePair<string, object>[] tags)
    {
        var key = new AggregatorKey(name, tags);
        ref var aggregator = ref CollectionsMarshal.GetValueRefOrAddDefault(Aggregators, key, out var exists);
        if (!exists)
        {
            aggregator = new HistogramAggregator(buckets, tags);
        }
        return aggregator;
    }

    private readonly KeyValuePair<string, object>[] _tags;
    private readonly HistogramBucketAggregator[] _buckets;
    private long _count;
    private long _sum;

    public HistogramAggregator(long[] buckets, KeyValuePair<string, object>[] tags)
    {
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

    public IEnumerable<Measurement<long>> CollectBuckets() => _buckets.Select(c => c.Collect());

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
