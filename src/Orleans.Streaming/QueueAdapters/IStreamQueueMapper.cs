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
        IEnumerable<QueueId> GetAllQueues();

        QueueId GetQueueForStream(StreamId streamId);
    }
}
