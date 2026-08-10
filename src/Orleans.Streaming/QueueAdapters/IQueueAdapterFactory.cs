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
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task<IQueueAdapter> CreateAdapter();

        /// <summary>
        /// Creates a queue adapter.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The queue adapter.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken) => CreateAdapter();
#pragma warning restore CS0618

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
