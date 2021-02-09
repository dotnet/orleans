
namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Aggregation dimensions for receiver monitor
    /// </summary>
    public class ReceiverMonitorDimensions
    {
        /// <summary>
        /// Eventhub partition
        /// </summary>
        public string QueueId { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="queueId"></param>
        public ReceiverMonitorDimensions(string queueId)
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

        public CacheMonitorDimensions(string queueId, string blockPoolId)
            :base(queueId)
        {
            this.BlockPoolId = blockPoolId;
        }
    }

    /// <summary>
    /// Aggregation dimensions for block pool monitors
    /// </summary>
    public class BlockPoolMonitorDimensions
    {
        /// <summary>
        /// Block pool Id
        /// </summary>
        public string BlockPoolId { get; set; }

        public BlockPoolMonitorDimensions(string blockPoolId)
        {
            this.BlockPoolId = blockPoolId;
        }
    }
}
