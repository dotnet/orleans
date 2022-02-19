using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Each queue message is allowed to be a heterogeneous, ordered set of events.
    /// <see cref="IBatchContainer"/> contains these events and allows users to query the batch for a specific type of event.
    /// </summary>
    public interface IBatchContainer
    {
        /// <summary>
        /// Ges the stream identifier for the stream this batch is part of.
        /// </summary>
        StreamId StreamId { get; }

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        IEnumerable<Tuple<T,StreamSequenceToken>> GetEvents<T>();

        /// <summary>
        /// Ges the stream sequence token for the start of this batch.
        /// </summary>
        StreamSequenceToken SequenceToken { get; }

        /// <summary>
        /// Gives an opportunity to <see cref="IBatchContainer"/> to set any data in the <see cref="RequestContext"/> before this <see cref="IBatchContainer"/> is sent to consumers.
        /// It can be the data that was set at the time event was generated and enqueued into the persistent provider or any other data.
        /// </summary>
        /// <returns><see langword="true"/> if the <see cref="RequestContext"/> was indeed modified, <see langword="false"/> otherwise.</returns>
        bool ImportRequestContext();
    }
}
