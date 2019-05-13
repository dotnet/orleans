using System;
using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// Converts event data to and from queue message
    /// </summary>
    public interface IQueueDataAdapter<TQueueMessage,TMessageBatch>
    {
        /// <summary>
        /// Creates a cloud queue message from stream event data.
        /// </summary>
        TQueueMessage ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext);

        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        TMessageBatch FromQueueMessage(TQueueMessage cloudMsg, long sequenceId);
    }
}
