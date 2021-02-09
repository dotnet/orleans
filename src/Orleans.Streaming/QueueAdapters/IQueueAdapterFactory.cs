using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Adapter factory.  This should create an adapter from the stream provider configuration
    /// </summary>
    public interface IQueueAdapterFactory
    {
        /// <summary>
        /// Create queue adapter.
        /// </summary>
        /// <returns></returns>
        Task<IQueueAdapter> CreateAdapter();

        /// <summary>
        /// Create queue message cache adapter
        /// </summary>
        /// <returns></returns>
        IQueueAdapterCache GetQueueAdapterCache();

        /// <summary>
        /// Create queue mapper
        /// </summary>
        /// <returns></returns>
        IStreamQueueMapper GetStreamQueueMapper();

        /// <summary>
        /// Acquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId);
    }
}
