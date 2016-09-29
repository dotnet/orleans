using System;


namespace Orleans.Runtime
{
    internal static class ApplicationRequestsStatisticsGroup
    {
        private static HistogramValueStatistic appRequestsLatencyHistogram;
        private const int NUM_APP_REQUESTS_EXP_LATENCY_HISTOGRAM_CATEGORIES = 31;
        private const int NUM_APP_REQUESTS_LINEAR_LATENCY_HISTOGRAM_CATEGORIES = 30000;

        private static CounterStatistic timedOutRequests;
        private static CounterStatistic totalAppRequests;
        private static CounterStatistic appRequestsTotalLatency;
        private static FloatValueStatistic appRequestsAverageLatency;
        
        internal static void Init(TimeSpan responseTimeout)
        {
            if (!StatisticsCollector.CollectApplicationRequestsStats) return;

            const CounterStorage storage = CounterStorage.LogAndTable;
            appRequestsLatencyHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram_ForTiming(
                StatisticNames.APP_REQUESTS_LATENCY_HISTOGRAM, NUM_APP_REQUESTS_EXP_LATENCY_HISTOGRAM_CATEGORIES);

            timedOutRequests = CounterStatistic.FindOrCreate(StatisticNames.APP_REQUESTS_TIMED_OUT, storage);
            totalAppRequests = CounterStatistic.FindOrCreate(StatisticNames.APP_REQUESTS_TOTAL_NUMBER_OF_REQUESTS, storage);
            appRequestsTotalLatency = CounterStatistic.FindOrCreate(StatisticNames.APP_REQUESTS_LATENCY_TOTAL, false, storage, true);

            appRequestsAverageLatency = FloatValueStatistic.FindOrCreate(
                    StatisticNames.APP_REQUESTS_LATENCY_AVERAGE,
                       () =>
                       {
                           long totalLatencyInTicks = appRequestsTotalLatency.GetCurrentValue();
                           if (totalLatencyInTicks == 0) return 0;
                           long numReqs = totalAppRequests.GetCurrentValue();
                           long averageLatencyInTicks = (long)((double)totalLatencyInTicks / (double)numReqs);
                           return (float)Utils.TicksToMilliSeconds(averageLatencyInTicks);
                       }, storage);
        }

        internal static void OnAppRequestsEnd(TimeSpan timeSpan)
        {
            appRequestsLatencyHistogram.AddData(timeSpan);
            appRequestsTotalLatency.IncrementBy(timeSpan.Ticks);
            totalAppRequests.Increment();
        }

        internal static void OnAppRequestsTimedOut()
        {
            timedOutRequests.Increment();
        }
    }
}
