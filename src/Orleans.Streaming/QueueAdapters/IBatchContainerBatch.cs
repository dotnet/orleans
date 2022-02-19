using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// A batch of queue messages (see IBatchContainer for description of batch contents)
    /// </summary>
    public interface IBatchContainerBatch : IBatchContainer
    {
        /// <summary>
        /// Gets the batch containers comprising this batch
        /// </summary>
        List<IBatchContainer> BatchContainers { get; }
    }
}