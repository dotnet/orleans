using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// Interface for In-memory stream queue grain.
    /// </summary>
    public interface IMemoryStreamQueueGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Set max event count. The total number of events enqueued can not exceed maxEventCount.
        /// </summary>
        Task SetMaxEventCount(int maxEventCount);

        /// <summary>
        /// Enqueue an event.
        /// </summary>
        Task Enqueue(MemoryEventData eventData);

        /// <summary>
        /// Dequeue up to maxCount events.
        /// </summary>
        Task<List<MemoryEventData>> Dequeue(int maxCount);
    }
}
