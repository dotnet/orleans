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

﻿using System.Diagnostics;

namespace Orleans.Runtime.Counters
{
    internal class PerfCounterConfigData
    {
        public StatisticName Name;
        public bool UseDeltaValue;
        internal ICounter<long> CounterStat;
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
            new PerfCounterConfigData {Name = StatisticNames.CATALOG_ACTIVATION_DUPLICATE_ACTIVATIONS},
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
        