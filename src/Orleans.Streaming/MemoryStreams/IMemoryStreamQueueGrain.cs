
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Providers
{
    /// <summary>
    /// Interface for In-memory stream queue grain.
    /// </summary>
    public interface IMemoryStreamQueueGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Enqueues an event.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("Enqueue")]
        Task Enqueue(MemoryMessageData data);

        /// <summary>
        /// Enqueues an event.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("74D60341")]
        Task Enqueue(MemoryMessageData data, CancellationToken cancellationToken) => Enqueue(data);

        /// <summary>
        /// Dequeues up to <paramref name="maxCount"/> events.
        /// </summary>
        /// <param name="maxCount">
        /// The maximum number of events to dequeue.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("Dequeue")]
        Task<List<MemoryMessageData>> Dequeue(int maxCount);

        /// <summary>
        /// Dequeues up to <paramref name="maxCount"/> events.
        /// </summary>
        /// <param name="maxCount">The maximum number of events to dequeue.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("7A8F8C1A")]
        Task<List<MemoryMessageData>> Dequeue(int maxCount, CancellationToken cancellationToken) => Dequeue(maxCount);
    }
}
