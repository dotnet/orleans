using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Providers.Streams.Common;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Base class for monitor aggregation dimensions, whcih is a information bag for the monitoring target. 
    /// Monitors can use this information bag to build its aggregation dimensions.
    /// </summary>
    public class EventHubMonitorAggregationDimensions : MonitorAggregationDimensions
    {
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
        public EventHubMonitorAggregationDimensions(GlobalConfiguration globalConfig, NodeConfiguration nodeConfig, string ehHubPath)
            :base(globalConfig, nodeConfig)
        {
            this.EventHubPath = ehHubPath;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        public EventHubMonitorAggregationDimensions(EventHubMonitorAggregationDimensions dimensions)
            :base(dimensions.GlobalConfig, dimensions.NodeConfig)
        {
            this.EventHubPath = dimensions.EventHubPath;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubMonitorAggregationDimensions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimensions for EventHubReceiverMonitor
    /// </summary>
    public class EventHubReceiverMonitorDimensions : EventHubMonitorAggregationDimensions
    {
        /// <summary>
        /// Eventhub partition
        /// </summary>
        public string EventHubPartition { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="ehPartition"></param>
        public EventHubReceiverMonitorDimensions(EventHubMonitorAggregationDimensions dimensions, string ehPartition)
            :base(dimensions)
        {
            this.EventHubPartition = ehPartition;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubReceiverMonitorDimensions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimensions for cache monitor used in Eventhub stream provider ecosystem
    /// </summary>
    public class EventHubCacheMonitorDimensions : EventHubReceiverMonitorDimensions
    {
        /// <summary>
        /// Block pool this cache belongs to
        /// </summary>
        public string BlockPoolId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="ehPartition"></param>
        /// <param name="blockPoolId"></param>
        public EventHubCacheMonitorDimensions(EventHubMonitorAggregationDimensions dimensions, string ehPartition, string blockPoolId)
            :base(dimensions, ehPartition)
        {
            this.BlockPoolId = blockPoolId;
        }

        /// <summary>
        /// Zero parametrers constructor
        /// </summary>
        public EventHubCacheMonitorDimensions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimensions for block pool monitor used in Eventhub stream provider ecosystem
    /// </summary>
    public class EventHubBlockPoolMonitorDimensions : EventHubMonitorAggregationDimensions
    {
        /// <summary>
        /// Block pool Id
        /// </summary>
        public string BlockPoolId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="blockPoolId"></param>
        public EventHubBlockPoolMonitorDimensions(EventHubMonitorAggregationDimensions dimensions, string blockPoolId)
            :base(dimensions)
        {
            this.BlockPoolId = blockPoolId;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public EventHubBlockPoolMonitorDimensions()
        {
        }
    }
}
