using System.Collections.Generic;
using Orleans.Runtime;


namespace Orleans.Streams
{
    /// <summary>
    /// The stream queue mapper is responsible for mapping ring ranges from the load balancing ring provider to stream queues.
    /// Implementation must be thread safe.
    /// </summary>
    public interface IConsistentRingStreamQueueMapper : IStreamQueueMapper
    {
        IEnumerable<QueueId> GetQueuesForRange(IRingRange range);
    }
}
