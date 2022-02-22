using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// The stream queue mapper returns a list of all queues and is also responsible for mapping streams to queues.
    /// Implementation must be thread safe.
    /// </summary>
    public interface IStreamQueueMapper
    {
        /// <summary>
        /// Gets all queues.
        /// </summary>
        /// <returns>All queues.</returns>
        IEnumerable<QueueId> GetAllQueues();

        /// <summary>
        /// Gets the queue for the specified stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The queue responsible for the specified stream.</returns>
        QueueId GetQueueForStream(StreamId streamId);
    }
}
