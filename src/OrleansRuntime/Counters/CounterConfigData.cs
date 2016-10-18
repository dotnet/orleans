namespace Orleans.Runtime.Counters
{
    internal class CounterConfigData
    {
        public StatisticName Name;
        public bool UseDeltaValue;
        internal ICounter<long> CounterStat;

        // TODO: Move this list to some kind of config file
        internal static readonly CounterConfigData[] StaticCounters =
        {
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION},
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE},
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DIRECT_SHUTDOWN},
            new CounterConfigData {Name = StatisticNames.DIRECTORY_CACHE_SIZE},
            new CounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_FULL_ISSUED, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.DIRECTORY_PARTITION_SIZE},
            new CounterConfigData {Name = StatisticNames.GATEWAY_CONNECTED_CLIENTS},
            new CounterConfigData {Name = StatisticNames.GATEWAY_LOAD_SHEDDING, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.GATEWAY_RECEIVED, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE},
            new CounterConfigData {Name = StatisticNames.MESSAGING_SENT_BYTES_TOTAL, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.MESSAGING_SENT_MESSAGES_TOTAL, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.MESSAGING_SENT_LOCALMESSAGES, UseDeltaValue = true},

            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.OneWay.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.Request.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData
            {
                Name =
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION,
                        Message.Directions.Response.ToString()),
                UseDeltaValue = true
            },
            new CounterConfigData {Name = StatisticNames.MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH},
            new CounterConfigData {Name = StatisticNames.MESSAGE_CENTER_SEND_QUEUE_LENGTH},
            new CounterConfigData {Name = StatisticNames.SCHEDULER_PENDINGWORKITEMS},
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_COUNT},
            new CounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_DUPLICATE_ACTIVATIONS},
            new CounterConfigData {Name = StatisticNames.RUNTIME_GC_TOTALMEMORYKB},
            new CounterConfigData {Name = StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS},
            new CounterConfigData {Name = StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS},
            new CounterConfigData {Name = StatisticNames.STORAGE_READ_TOTAL, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.STORAGE_WRITE_TOTAL, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.STORAGE_ACTIVATE_TOTAL, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.STORAGE_READ_ERRORS, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.STORAGE_WRITE_ERRORS, UseDeltaValue = true},
            new CounterConfigData {Name = StatisticNames.AZURE_SERVER_BUSY, UseDeltaValue = true},
        };
    }
}
