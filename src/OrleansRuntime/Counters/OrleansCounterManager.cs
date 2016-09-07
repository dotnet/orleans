using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Counters
{
    internal static class OrleansCounterManager
    {
        private static readonly Logger logger = LogManager.GetLogger("OrleansPerfCounterManager", LoggerType.Runtime);
        //private static readonly List<Tuple<StatisticName, bool>> counterNames = new List<Tuple<StatisticName, bool>>();

        //public static void PrecreateCounters()
        //{
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DIRECT_SHUTDOWN, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.DIRECTORY_CACHE_SIZE, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.DIRECTORY_LOOKUPS_FULL_ISSUED, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.DIRECTORY_PARTITION_SIZE, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.GATEWAY_CONNECTED_CLIENTS, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.GATEWAY_LOAD_SHEDDING, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.GATEWAY_RECEIVED, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MESSAGING_SENT_BYTES_TOTAL, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MESSAGING_SENT_MESSAGES_TOTAL, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MESSAGING_SENT_LOCALMESSAGES, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
        //                Message.Directions.OneWay.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
        //                Message.Directions.Request.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
        //                Message.Directions.Response.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
        //               Message.Directions.OneWay.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
        //               Message.Directions.Request.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
        //               Message.Directions.Response.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
        //               Message.Directions.OneWay.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
        //               Message.Directions.Request.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
        //               Message.Directions.Response.ToString()), true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.MESSAGE_CENTER_SEND_QUEUE_LENGTH, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.SCHEDULER_PENDINGWORKITEMS, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_COUNT, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.CATALOG_ACTIVATION_DUPLICATE_ACTIVATIONS, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.RUNTIME_GC_TOTALMEMORYKB, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS, false));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.STORAGE_READ_TOTAL, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.STORAGE_WRITE_TOTAL, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.STORAGE_ACTIVATE_TOTAL, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.STORAGE_READ_ERRORS, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.STORAGE_WRITE_ERRORS, true));
        //    counterNames.Add(new Tuple<StatisticName, bool>(StatisticNames.AZURE_SERVER_BUSY, true));
        //}

        public static int WriteCounters()
        {
            if (logger.IsVerbose) logger.Verbose("Writing Windows perf counters.");

            int numWriteErrors = 0;

            foreach (CounterConfigData cd in CounterConfigData.StaticCounters)
            {
                StatisticName name = cd.Name;
                string perfCounterName = GetPerfCounterName(cd);

                try
                {
                    if (logger.IsVerbose3) logger.Verbose3(ErrorCode.PerfCounterWriting, "Writing perf counter {0}", perfCounterName);

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
                    logger.Error(ErrorCode.PerfCounterUnableToWrite, string.Format("Unable to write to Windows perf counter '{0}'", name), ex);
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
