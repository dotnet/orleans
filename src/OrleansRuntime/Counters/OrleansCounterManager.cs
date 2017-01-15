
using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Counters
{
    internal static class OrleansCounterManager
    {
        private static readonly Logger logger = LogManager.GetLogger("OrleansCounterManager", LoggerType.Runtime);

        public static int WriteCounters()
        {
            if (logger.IsVerbose) logger.Verbose("Writing counters.");

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
                    if (logger.IsVerbose3) logger.Verbose3(ErrorCode.PerfCounterWriting, "Writing counter {0}", counter.Name);

                    counter.TrackMetric(logger);
                }
                catch (Exception ex)
                {
                    numWriteErrors++;
                    logger.Error(ErrorCode.PerfCounterUnableToWrite, $"Unable to write to counter '{counter.Name}'", ex);
                }
            }
            return numWriteErrors;
        }
    }
}
