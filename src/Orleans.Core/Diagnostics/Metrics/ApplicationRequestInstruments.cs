using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Orleans.Runtime;

internal static class ApplicationRequestInstruments
{
    internal static Counter<long> TimedOutRequestsCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);

    private static readonly long[] AppRequestsLatencyHistogramBuckets = new double[] { 0.1, 1, 2, 4, 6, 8, 10, 50, 100, 200, 400, 800, 1000, 1500, 2000, 5000, 10000, 15000 }
        .Select(x => (long)(x * TimeSpan.TicksPerMillisecond))
        .ToArray();
    private static readonly HistogramAggregator AppRequestsLatencyHistogramAggregator = new(AppRequestsLatencyHistogramBuckets, Array.Empty<KeyValuePair<string, object>>());
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramBucket = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-bucket", AppRequestsLatencyHistogramAggregator.CollectBuckets);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramCount = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-count", AppRequestsLatencyHistogramAggregator.CollectCount);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramSum = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-sum", AppRequestsLatencyHistogramAggregator.CollectSum);


    internal static void OnAppRequestsEnd(TimeSpan timeSpan)
    {
        if (AppRequestsLatencyHistogramSum.Enabled)
            AppRequestsLatencyHistogramAggregator.Record(timeSpan.Ticks);
    }

    internal static void OnAppRequestsTimedOut()
    {
        TimedOutRequestsCounter.Add(1);
    }
}
