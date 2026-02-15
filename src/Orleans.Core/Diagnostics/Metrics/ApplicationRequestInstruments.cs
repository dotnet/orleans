using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal class ApplicationRequestInstruments
{
    private readonly Counter<long> _timedOutRequestsCounter;
    private readonly Counter<long> _canceledRequestsCounter;

    private static readonly long[] AppRequestsLatencyHistogramBuckets = [1, 2, 4, 6, 8, 10, 50, 100, 200, 400, 800, 1_000, 1_500, 2_000, 5_000, 10_000, 15_000];
    private readonly HistogramAggregator _appRequestsLatencyHistogramAggregator;
    private readonly ObservableCounter<long> _appRequestsLatencyHistogramBucket;
    private readonly ObservableCounter<long> _appRequestsLatencyHistogramCount;
    private readonly ObservableCounter<long> _appRequestsLatencyHistogramSum;

    internal ApplicationRequestInstruments(OrleansInstruments instruments)
    {
        _timedOutRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);
        _canceledRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_CANCELED);
        _appRequestsLatencyHistogramAggregator = new(AppRequestsLatencyHistogramBuckets, [], value => new("duration", $"{value}ms"));
        _appRequestsLatencyHistogramBucket = instruments.Meter.CreateObservableCounter(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-bucket", _appRequestsLatencyHistogramAggregator.CollectBuckets);
        _appRequestsLatencyHistogramCount = instruments.Meter.CreateObservableCounter(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-count", _appRequestsLatencyHistogramAggregator.CollectCount);
        _appRequestsLatencyHistogramSum = instruments.Meter.CreateObservableCounter(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-sum", _appRequestsLatencyHistogramAggregator.CollectSum);
    }

    internal void OnAppRequestsEnd(long durationMilliseconds)
    {
        if (_appRequestsLatencyHistogramSum.Enabled)
            _appRequestsLatencyHistogramAggregator.Record(durationMilliseconds);
    }

    internal void OnAppRequestsTimedOut()
    {
        _timedOutRequestsCounter.Add(1);
    }

    internal void OnAppRequestsCanceled()
    {
        _canceledRequestsCounter.Add(1);
    }
}
