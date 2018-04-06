using Orleans.Runtime;
using System.Diagnostics;

namespace OrleansTelemetryConsumers.Counters
{
    internal class PerfCounterConfigData
    {
        public StatisticName Name;
        public bool UseDeltaValue;
        internal PerformanceCounter PerfCounter;

        // TODO: Move this list to some kind of config file
        internal static readonly PerfCounterConfigData[] StaticPerfCounters =
        {
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION},
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE},
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DIRECT_SHUTDOWN},
            new PerfCounterConfigData {Name = StatisticNames.DIRECTORY_CACHE_SIZE},
            new PerfCounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_FULL_ISSUED, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.DIRECTORY_PARTITION_SIZE},
            new PerfCounterConfigData {Name = StatisticNames.GATEWAY_CONNECTED_CLIENTS},
            new PerfCounterConfigData {Name = StatisticNames.GATEWAY_LOAD_SHEDDING, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.GATEWAY_RECEIVED, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE},
            new PerfCounterConfigData {Name = StatisticNames.MESSAGING_SENT_BYTES_TOTAL, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.MESSAGING_SENT_MESSAGES_TOTAL, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.MESSAGING_SENT_LOCALMESSAGES, UseDeltaValue = true},

            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new PerfCounterConfigData {Name = StatisticNames.MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH},
            new PerfCounterConfigData {Name = StatisticNames.MESSAGE_CENTER_SEND_QUEUE_LENGTH},
            new PerfCounterConfigData {Name = StatisticNames.SCHEDULER_PENDINGWORKITEMS},
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_COUNT},
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS},
            new PerfCounterConfigData {Name = StatisticNames.RUNTIME_GC_TOTALMEMORYKB},
            new PerfCounterConfigData {Name = StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS},
            new PerfCounterConfigData {Name = StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS},
            new PerfCounterConfigData {Name = StatisticNames.STORAGE_READ_TOTAL, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.STORAGE_WRITE_TOTAL, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.STORAGE_ACTIVATE_TOTAL, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.STORAGE_READ_ERRORS, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.STORAGE_WRITE_ERRORS, UseDeltaValue = true},
            new PerfCounterConfigData {Name = StatisticNames.AZURE_SERVER_BUSY, UseDeltaValue = true},
        };
    }
}
