using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;


namespace Orleans.Runtime
{
    internal class ApplicationRequestsStatisticsGroup
    {
        private static HistogramValueStatistic appRequestsLatencyHistogram;
        private const int NUM_APP_REQUESTS_EXP_LATENCY_HISTOGRAM_CATEGORIES = 31;
        private const int NUM_APP_REQUESTS_LINEAR_LATENCY_HISTOGRAM_CATEGORIES = 30000;

        private static CounterStatistic timedOutRequests;
        private static CounterStatistic totalAppRequests;
        private static CounterStatistic appRequestsTotalLatency;
        private static FloatValueStatistic appRequestsAverageLatency;
        
        public ApplicationRequestsStatisticsGroup(IOptions<StatisticsOptions> statisticsOptions)
        {
            this.CollectApplicationRequestsStats = statisticsOptions.Value.CollectionLevel.CollectApplicationRequestsStats();

            if (!this.CollectApplicationRequestsStats) return;

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

        public bool CollectApplicationRequestsStats { get; }

        internal void OnAppRequestsEnd(TimeSpan timeSpan)
        {
            if (!this.CollectApplicationRequestsStats) return;

            appRequestsLatencyHistogram?.AddData(timeSpan);
            appRequestsTotalLatency?.IncrementBy(timeSpan.Ticks);
            totalAppRequests?.Increment();
        }

        internal void OnAppRequestsTimedOut()
        {
            if (!this.CollectApplicationRequestsStats) return;

            timedOutRequests?.Increment();
        }
    }
}
