using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal class ApplicationRequestInstruments
{
    private const string MillisecondsUnit = "ms";

#if NET10_0_OR_GREATER
    private static readonly double[] AppRequestsLatencyHistogramBuckets =
    [
        0.1, 0.25, 0.5, 0.75,
        1, 2, 4, 6, 8, 10, 50, 100,
        200, 400, 800, 1_000, 1_500, 2_000,
        5_000, 10_000, 15_000, 30_000
    ];
#endif

    private readonly Counter<long> _timedOutRequestsCounter;
    private readonly Counter<long> _canceledRequestsCounter;
    private readonly Histogram<double> _appRequestsLatencyHistogram;

    internal ApplicationRequestInstruments(OrleansInstruments instruments)
    {
        _timedOutRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);
        _canceledRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_CANCELED);
#if NET10_0_OR_GREATER
        _appRequestsLatencyHistogram = instruments.Meter.CreateHistogram<double>(
            InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM,
            MillisecondsUnit,
            advice: new() { HistogramBucketBoundaries = AppRequestsLatencyHistogramBuckets });
#else
        _appRequestsLatencyHistogram = instruments.Meter.CreateHistogram<double>(
            InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM,
            MillisecondsUnit);
#endif
    }

    internal void OnAppRequestsEnd(double durationMilliseconds) => _appRequestsLatencyHistogram.Record(durationMilliseconds);

    internal bool AppRequestsLatencyEnabled => _appRequestsLatencyHistogram.Enabled;

    internal void OnAppRequestsTimedOut(string grainType)
    {
        _timedOutRequestsCounter.Add(1, new KeyValuePair<string, object?>("grain_type", grainType));
    }

    internal void OnAppRequestsCanceled(string grainType)
    {
        _canceledRequestsCounter.Add(1, new KeyValuePair<string, object?>("grain_type", grainType));
    }
}
