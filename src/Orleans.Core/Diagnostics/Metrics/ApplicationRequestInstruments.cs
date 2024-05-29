#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal static class ApplicationRequestInstruments
{
    internal static Counter<long> TimedOutRequestsCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);
    private static long _totalRequests;

    private static readonly ObservableCounter<long> RequestsPerSecondCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.REQUESTS_COMPLETED, () => Volatile.Read(ref _totalRequests));

    /*
    private static readonly long[] AppRequestsLatencyHistogramBuckets = new long[] { 1, 2, 4, 6, 8, 10, 50, 100, 200, 400, 800, 1_000, 1_500, 2_000, 5_000, 10_000, 15_000 };
    private static readonly HistogramAggregator AppRequestsLatencyHistogramAggregator = new(AppRequestsLatencyHistogramBuckets, Array.Empty<KeyValuePair<string, object>>(), value => new ("duration", $"{value}ms"));
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramBucket = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-bucket", AppRequestsLatencyHistogramAggregator.CollectBuckets);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramCount = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-count", AppRequestsLatencyHistogramAggregator.CollectCount);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramSum = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-sum", AppRequestsLatencyHistogramAggregator.CollectSum);
    */
    private static readonly Histogram<long> ResponseTimeHistogram = Instruments.Meter.CreateHistogram<long>("response-time", "ms");

    internal static void OnAppRequestsEnd(long durationMilliseconds)
    {
        if (RequestsPerSecondCounter.Enabled)
        {
            Interlocked.Increment(ref _totalRequests);
        }
        if (ResponseTimeHistogram.Enabled)
        {
            ResponseTimeHistogram.Record(durationMilliseconds);
        }
        /*
        if (AppRequestsLatencyHistogramSum.Enabled)
        {
            AppRequestsLatencyHistogramAggregator.Record(durationMilliseconds);
        }
        */
    }

    internal static void OnAppRequestsTimedOut()
    {
        TimedOutRequestsCounter.Add(1);
    }
}
