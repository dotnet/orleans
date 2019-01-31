using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streams
{
    /// <summary>
    /// A batch of batch containers, that if configured (see StreamPullingAgentOptions), will be the data pulled by the
    /// PersistenStreamPullingAgent from it's underlying cache
    /// </summary>
    class BatchContainerBatch : IBatchContainerBatch
    {
        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// Derived from the first batch container in the batch.
        /// </summary>
        public Guid StreamGuid { get; }

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// Derived from the first batch container in the batch.
        /// </summary>
        public StreamSequenceToken SequenceToken { get; }

        /// <summary>
        /// Stream namespace for the stream this batch is part of.
        /// Derived from the first batch container in the batch.
        /// </summary>
        public string StreamNamespace { get; }

        /// <summary>
        /// Batch containers comprising this batch
        /// </summary>
        public List<IBatchContainer> BatchContainers { get; }

        public BatchContainerBatch(List<IBatchContainer> batchContainers)
        {
            if ((batchContainers == null) || !batchContainers.Any())
            {
                throw new ArgumentNullException(nameof(batchContainers));
            }

            this.BatchContainers = batchContainers;

            var containerDelegate = this.BatchContainers.First();
            this.SequenceToken = containerDelegate.SequenceToken;
            this.StreamGuid = containerDelegate.StreamGuid;
            this.StreamNamespace = containerDelegate.StreamNamespace;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return this.BatchContainers.SelectMany(batchContainer => batchContainer.GetEvents<T>());
        }

        public bool ImportRequestContext()
        {
            return this.BatchContainers.First().ImportRequestContext();
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            // ShouldDeliver is called on a per IBatchContainer basis for each IBatchContainer that composes this BatchContainerBatch.
            // Therefore, no filtering is done on the BatchContainerBatch level.
            return true;
        }

    }
}
