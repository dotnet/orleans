using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{

    /// <summary>
    /// Converts event data to queue message
    /// </summary>
    public interface IQueueDataAdapter<TQueueMessage>
    {
        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        TQueueMessage ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext);
    }

    /// <summary>
    /// Converts event data to and from queue message
    /// </summary>
    public interface IQueueDataAdapter<TQueueMessage, TMessageBatch> : IQueueDataAdapter<TQueueMessage>
    {
        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        TMessageBatch FromQueueMessage(TQueueMessage queueMessage, long sequenceId);
    }
}
