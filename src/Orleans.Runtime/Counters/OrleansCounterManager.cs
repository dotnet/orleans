
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Counters
{
    internal static class OrleansCounterManager
    {
        public static int WriteCounters(ITelemetryProducer telemetryProducer, ILogger logger)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Writing counters.");

            int numWriteErrors = 0;

            List<ICounter> allCounters = new List<ICounter>();
            CounterStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            IntValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            StringValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            FloatValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            AverageTimeSpanStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);

            foreach (ICounter counter in allCounters)
            {
                try
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)ErrorCode.PerfCounterWriting, "Writing counter {0}", counter.Name);

                    counter.TrackMetric(telemetryProducer);
                }
                catch (Exception ex)
                {
                    numWriteErrors++;
                    logger.LogError((int)ErrorCode.PerfCounterUnableToWrite, ex, $"Unable to write to counter '{counter.Name}'");
                }
            }
            return numWriteErrors;
        }
    }
}
