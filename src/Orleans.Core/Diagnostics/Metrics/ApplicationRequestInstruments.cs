using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class ApplicationRequestInstruments
{
    internal static Histogram<double> AppRequestsLatencyHistogram = Instruments.Meter.CreateHistogram<double>(StatisticNames.APP_REQUESTS_LATENCY_HISTOGRAM, "ms");
    internal static Counter<long> TimedOutRequestsCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.APP_REQUESTS_TIMED_OUT);

    internal static void OnAppRequestsEnd(TimeSpan timeSpan)
    {
        // if (!appRequestsLatencyHistogram.Enabled) return;
        AppRequestsLatencyHistogram.Record(timeSpan.TotalMilliseconds);
    }

    internal static void OnAppRequestsTimedOut()
    {
        TimedOutRequestsCounter.Add(1);
    }
}
