using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime;

internal class HistogramBucketAggregator
{
    private long _count = 0;
    private readonly KeyValuePair<string, object>[] _tags;
    public long Bound { get; }
    public HistogramBucketAggregator(KeyValuePair<string, object>[] tags, long bound)
    {
        _tags = tags.Concat(new[] { new KeyValuePair<string, object>("bucket", bound) }).ToArray();
        Bound = bound;
    }

    public void Add(long measurement) => Interlocked.Add(ref _count, measurement);

    public Measurement<long> Collect()
    {
        var sum = Interlocked.Exchange(ref _count, 0);
        return new Measurement<long>(sum, _tags);
    }
}
