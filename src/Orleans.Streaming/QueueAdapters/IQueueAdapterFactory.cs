using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Adapter factory. This should create an adapter from the stream provider configuration
    /// </summary>
    public interface IQueueAdapterFactory
    {
        /// <summary>
        /// Creates a queue adapter.
        /// </summary>
        /// <returns>The queue adapter</returns>
        Task<IQueueAdapter> CreateAdapter();

        /// <summary>
        /// Creates a queue adapter.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The queue adapter.</returns>
        Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateAdapter().WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Creates queue message cache adapter.
        /// </summary>
        /// <returns>The queue adapter cache.</returns>
        IQueueAdapterCache GetQueueAdapterCache();

        /// <summary>
        /// Creates a queue mapper.
        /// </summary>
        /// <returns>The queue mapper.</returns>
        IStreamQueueMapper GetStreamQueueMapper();

        /// <summary>
        /// Acquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>The stream failure handler.</returns>
        Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId);
    }
}
