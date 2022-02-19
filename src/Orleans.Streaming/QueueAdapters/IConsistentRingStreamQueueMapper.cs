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
        /// <summary>
        /// Gets the queues which map to the specified range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <returns>The queues which map to the specified range.</returns>
        IEnumerable<QueueId> GetQueuesForRange(IRingRange range);
    }
}
