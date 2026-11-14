using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal class ApplicationRequestInstruments
{
    private const string MillisecondsUnit = "ms";

    private readonly Counter<long> _timedOutRequestsCounter;
    private readonly Counter<long> _canceledRequestsCounter;
    private readonly Histogram<double> _appRequestsLatencyHistogram;

    internal ApplicationRequestInstruments(OrleansInstruments instruments)
    {
        _timedOutRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);
        _canceledRequestsCounter = instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_CANCELED);
        _appRequestsLatencyHistogram = instruments.Meter.CreateHistogram<double>(
            InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM,
            MillisecondsUnit);
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
