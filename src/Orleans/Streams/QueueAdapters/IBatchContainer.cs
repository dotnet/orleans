using System;
using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// Each queue message is allowed to be a heterogeneous  ordered set of events.  IBatchContainer contains these events and allows users to query the batch for a specific type of event.
    /// </summary>
    public interface IBatchContainer
    {
        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        Guid StreamGuid { get; }

        /// <summary>
        /// Stream namespace for the stream this batch is part of.
        /// </summary>
        String StreamNamespace { get; }

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        IEnumerable<Tuple<T,StreamSequenceToken>> GetEvents<T>();

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        StreamSequenceToken SequenceToken { get; }

        /// <summary>
        /// Gives an opportunity to IBatchContainer to set any data in the RequestContext before this IBatchContainer is sent to consumers.
        /// It can be the data that was set at the time event was generated and enqueued into the persistent provider or any other data.
        /// </summary>
        /// <returns>True if the RequestContext was indeed modified, false otherwise.</returns>
        bool ImportRequestContext();

        /// <summary>
        /// Decide whether this batch should be sent to the specified target.
        /// </summary>
        bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc);
    }
}
