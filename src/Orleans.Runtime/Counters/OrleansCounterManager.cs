
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Counters
{
    internal static class OrleansCounterManager
    {
        public static int WriteCounters(ITelemetryProducer telemetryProducer, ILogger logger)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Writing counters");

            int numWriteErrors = 0;

            List<ICounter> allCounters = new List<ICounter>();
            CounterStatistic.AddCounters(allCounters);
            IntValueStatistic.AddCounters(allCounters);
            StringValueStatistic.AddCounters(allCounters);
            FloatValueStatistic.AddCounters(allCounters);
            AverageTimeSpanStatistic.AddCounters(allCounters);

            foreach (ICounter counter in allCounters)
            {
                try
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)ErrorCode.PerfCounterWriting, "Writing counter {CounterName}", counter.Name);

                    counter.TrackMetric(telemetryProducer);
                }
                catch (Exception ex)
                {
                    numWriteErrors++;
                    logger.LogError((int)ErrorCode.PerfCounterUnableToWrite, ex, "Unable to write to counter '{CounterName}'", counter.Name);
                }
            }
            return numWriteErrors;
        }
    }
}
