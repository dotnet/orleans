using System;
using System.Collections.Generic;


namespace Orleans.Streams
{
    /// <summary>
    /// The stream queue mapper returns a list of all queues and is also responsible for mapping streams to queues.
    /// Implementation must be thread safe.
    /// </summary>
    public interface IStreamQueueMapper
    {
        IEnumerable<QueueId> GetAllQueues();

        QueueId GetQueueForStream(Guid streamGuid, String streamNamespace);
    }
}
