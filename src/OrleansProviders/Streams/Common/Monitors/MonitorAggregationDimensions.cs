using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Base class for holding monitor aggregation dimensions
    /// </summary>
    public class MonitorAggregationDimensions
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
        /// Constructor
        /// </summary>
        /// <param name="globalConfig"></param>
        /// <param name="nodeConfig"></param>
        public MonitorAggregationDimensions(GlobalConfiguration globalConfig, NodeConfiguration nodeConfig)
        {
            this.GlobalConfig = globalConfig;
            this.NodeConfig = nodeConfig;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public MonitorAggregationDimensions()
        { }
    }

    /// <summary>
    /// Aggregation dimensions for receiver monitor
    /// </summary>
    public class ReceiverMonitorDimensions : MonitorAggregationDimensions
    {
        /// <summary>
        /// Eventhub partition
        /// </summary>
        public string QueueId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="queueId"></param>
        public ReceiverMonitorDimensions(MonitorAggregationDimensions dimensions, string queueId)
            : base(dimensions.GlobalConfig, dimensions.NodeConfig)
        {
            this.QueueId = queueId;
        }

        /// <summary>
        /// Zero parameter constructor
        /// </summary>
        public ReceiverMonitorDimensions()
        {
        }
    }

    /// <summary>
    /// Aggregation dimensions for cache monitor
    /// </summary>
    public class CacheMonitorDimensions : ReceiverMonitorDimensions
    {
        /// <summary>
        /// Block pool Id
        /// </summary>
        public string BlockPoolId { get; set; }

        public CacheMonitorDimensions(MonitorAggregationDimensions dimensions, string queueId, string blockPoolId)
            :base(dimensions, queueId)
        {
            this.BlockPoolId = blockPoolId;
        }
    }

    /// <summary>
    /// Aggregation dimensions for block pool monitors
    /// </summary>
    public class BlockPoolMonitorDimensions : MonitorAggregationDimensions
    {
        /// <summary>
        /// Block pool Id
        /// </summary>
        public string BlockPoolId { get; set; }

        public BlockPoolMonitorDimensions(MonitorAggregationDimensions dimensions, string blockPoolId)
            :base(dimensions.GlobalConfig, dimensions.NodeConfig)
        {
            this.BlockPoolId = blockPoolId;
        }
    }
}
