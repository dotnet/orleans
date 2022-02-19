namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Aggregation dimensions for receiver monitor.
    /// </summary>
    public class ReceiverMonitorDimensions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReceiverMonitorDimensions"/> class.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        public ReceiverMonitorDimensions(string queueId)
        {
            this.QueueId = queueId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReceiverMonitorDimensions"/> class.
        /// </summary>
        public ReceiverMonitorDimensions()
        {
        }

        /// <summary>
        /// Gets the queue identifier.
        /// </summary>
        public string QueueId { get; set; }
    }

    /// <summary>
    /// Aggregation dimensions for cache monitor.
    /// </summary>
    public class CacheMonitorDimensions : ReceiverMonitorDimensions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CacheMonitorDimensions"/> class.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <param name="blockPoolId">The block pool identifier.</param>
        public CacheMonitorDimensions(string queueId, string blockPoolId)
            :base(queueId)
        {
            this.BlockPoolId = blockPoolId;
        }

        /// <summary>
        /// Gets or sets the block pool identifier.
        /// </summary>
        /// <value>The block pool identifier.</value>
        public string BlockPoolId { get; set; }
    }

    /// <summary>
    /// Aggregation dimensions for block pool monitors.
    /// </summary>
    public class BlockPoolMonitorDimensions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BlockPoolMonitorDimensions"/> class.
        /// </summary>
        /// <param name="blockPoolId">The block pool identifier.</param>
        public BlockPoolMonitorDimensions(string blockPoolId)
        {
            this.BlockPoolId = blockPoolId;
        }

        /// <summary>
        /// Gets or sets the block pool identifier.
        /// </summary>
        /// <value>The block pool identifier.</value>
        public string BlockPoolId { get; set; }
    }
}
