
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
        /// Enqueue an event.
        /// </summary>
        Task Enqueue(MemoryMessageData data);

        /// <summary>
        /// Dequeue up to maxCount events.
        /// </summary>
        Task<List<MemoryMessageData>> Dequeue(int maxCount);
    }
}
