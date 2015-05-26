/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
