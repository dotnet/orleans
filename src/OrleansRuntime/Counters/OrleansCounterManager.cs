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

            foreach (CounterConfigData cd in CounterConfigData.StaticCounters)
            {
                StatisticName name = cd.Name;
                string perfCounterName = GetPerfCounterName(cd);

                try
                {
                    if (logger.IsVerbose3) logger.Verbose3(ErrorCode.PerfCounterWriting, "Writing counter {0}", perfCounterName);

                    if (cd.CounterStat == null)
                    {
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.PerfCounterRegistering, "Searching for statistic {0}", name);
                        ICounter<long> ctr = IntValueStatistic.Find(name);
                        cd.CounterStat = ctr ?? CounterStatistic.FindOrCreate(name);
                    }

                    if (cd.CounterStat != null)
                        logger.TrackMetric(perfCounterName, cd.CounterStat.GetCurrentValue());
                }
                catch (Exception ex)
                {
                    numWriteErrors++;
                    logger.Error(ErrorCode.PerfCounterUnableToWrite, string.Format("Unable to write to counter '{0}'", name), ex);
                }
            }
            return numWriteErrors;
        }

        private static string GetPerfCounterName(CounterConfigData cd)
        {
            return cd.Name.Name + "." + (cd.UseDeltaValue ? "Delta" : "Current");
        }
    }
}
