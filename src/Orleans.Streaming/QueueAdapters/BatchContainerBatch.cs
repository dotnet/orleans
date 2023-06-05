using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// A batch of batch containers, that if configured (see StreamPullingAgentOptions), will be the data pulled by the
    /// PersistenStreamPullingAgent from it's underlying cache
    /// </summary>
    [GenerateSerializer]
    public sealed class BatchContainerBatch : IBatchContainerBatch
    {
        /// <summary>
        /// Gets the stream identifier for the stream this batch is part of.
        /// Derived from the first batch container in the batch.
        /// </summary>
        [Id(0)]
        public StreamId StreamId { get; }

        /// <summary>
        /// Gets the stream Sequence Token for the start of this batch.
        /// Derived from the first batch container in the batch.
        /// </summary>
        [Id(1)]
        public StreamSequenceToken SequenceToken { get; }

        /// <summary>
        /// Gets the batch containers comprising this batch
        /// </summary>
        [Id(2)]
        public List<IBatchContainer> BatchContainers { get; }
                
        public BatchContainerBatch(List<IBatchContainer> batchContainers)
        {
            if ((batchContainers == null) || !batchContainers.Any())
            {
                throw new ArgumentNullException(nameof(batchContainers));
            }

            BatchContainers = batchContainers;

            var containerDelegate = BatchContainers[0];
            SequenceToken = containerDelegate.SequenceToken;
            StreamId = containerDelegate.StreamId;
        }

        /// <inheritdoc/>
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => BatchContainers.SelectMany(batchContainer => batchContainer.GetEvents<T>());

        /// <inheritdoc/>
        public bool ImportRequestContext() => BatchContainers[0].ImportRequestContext();
    }
}
