using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Base class for monitor aggregation dimentions, whcih is a information bag for the monitoring target. 
    /// Monitors can use this information bag to build its aggregation dimentions.
    /// </summary>
    public class EventHubMonitorAggregationDimentions
    {
        /// <summary>
        /// Data object holding Silo global configuration parameters.
        /// </summary>
        public GlobalConfiguration GlobalConfig { get; set; }

        /// <summary>
        /// Individual node-specific silo configuration parameters.
        /// </summary>
        public NodeConfiguration NodeConfig { get; set; }

        /// <summary>
        /// Eventhub path
        /// </summary>
        public string EventHubPath { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="globalConfig"></param>
        /// <param name="nodeConfig"></param>
        /// <param name="ehHubPath"></param>
        public EventHubMonitorAggregationDimentions(GlobalConfiguration globalConfig, NodeConfiguration nodeConfig, string ehHubPath)
        {
            this.GlobalConfig = globalConfig;
            this.NodeConfig = nodeConfig;
            this.EventHubPath = ehHubPath;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        public EventHubMonitorAggregationDimentions(EventHubMonitorAggregationDimentions dimentions)
        {
            this.GlobalConfig = dimentions.GlobalConfig;
            this.NodeConfig = dimentions.NodeConfig;
            this.EventHubPath = dimentions.EventHubPath;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubMonitorAggregationDimentions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimentions for EventHubReceiverMonitor
    /// </summary>
    public class EventHubReceiverMonitorDimentions : EventHubMonitorAggregationDimentions
    {
        /// <summary>
        /// Eventhub partition
        /// </summary>
        public string EventHubPartition { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        /// <param name="ehPartition"></param>
        public EventHubReceiverMonitorDimentions(EventHubMonitorAggregationDimentions dimentions, string ehPartition)
            :base(dimentions)
        {
            this.EventHubPartition = ehPartition;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubReceiverMonitorDimentions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimentions for cache monitor used in Eventhub stream provider ecosystem
    /// </summary>
    public class EventHubCacheMonitorDimentions : EventHubReceiverMonitorDimentions
    {
        /// <summary>
        /// Block pool this cache belongs to
        /// </summary>
        public string ObjectPoolId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        /// <param name="ehPartition"></param>
        /// <param name="blockPoolId"></param>
        public EventHubCacheMonitorDimentions(EventHubMonitorAggregationDimentions dimentions, string ehPartition, string blockPoolId)
            :base(dimentions, ehPartition)
        {
            this.ObjectPoolId = blockPoolId;
        }

        /// <summary>
        /// Zero parametrers constructor
        /// </summary>
        public EventHubCacheMonitorDimentions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimentions for block pool monitor used in Eventhub stream provider ecosystem
    /// </summary>
    public class EventHubObjectPoolMonitorDimentions : EventHubMonitorAggregationDimentions
    {
        /// <summary>
        /// Block pool Id
        /// </summary>
        public string ObjectPoolId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions"></param>
        /// <param name="objectPooolId"></param>
        public EventHubObjectPoolMonitorDimentions(EventHubMonitorAggregationDimentions dimentions, string objectPooolId)
            :base(dimentions)
        {
            this.ObjectPoolId = objectPooolId;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubObjectPoolMonitorDimentions()
        {
        }
    }
}
