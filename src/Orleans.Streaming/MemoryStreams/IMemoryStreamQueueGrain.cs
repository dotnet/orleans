
using System.Collections.Generic;
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
        Task Enqueue(MemoryMessageData data);

        /// <summary>
        /// Dequeues up to <paramref name="maxCount"/> events.
        /// </summary>
        /// <param name="maxCount">
        /// The maximum number of events to dequeue.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task<List<MemoryMessageData>> Dequeue(int maxCount);
    }
}
