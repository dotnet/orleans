using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
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

    public class CacheMonitorDimensions : ReceiverMonitorDimensions
    {
        public string BlockPoolId { get; set; }

        public CacheMonitorDimensions(MonitorAggregationDimensions dimensions, string queueId, string blockPoolId)
            :base(dimensions, queueId)
        {
            this.BlockPoolId = blockPoolId;
        }
    }

    public class BlockPoolMonitorDimensions : MonitorAggregationDimensions
    {
        public string BlockPoolId { get; set; }

        public BlockPoolMonitorDimensions(MonitorAggregationDimensions dimensions, string blockPoolId)
            :base(dimensions.GlobalConfig, dimensions.NodeConfig)
        {
            this.BlockPoolId = blockPoolId;
        }
    }
}
