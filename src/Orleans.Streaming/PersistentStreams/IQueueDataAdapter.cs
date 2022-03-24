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
        /// <typeparam name="T">The stream event type.</typeparam>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="events">The events.</param>
        /// <param name="token">The token.</param>
        /// <param name="requestContext">The request context.</param>
        /// <returns>A new queue message.</returns>
        TQueueMessage ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext);
    }

    /// <summary>
    /// Converts event data to and from queue message
    /// </summary>
    /// <typeparam name="TQueueMessage">The type of the queue message.</typeparam>
    /// <typeparam name="TMessageBatch">The type of the message batch.</typeparam>
    public interface IQueueDataAdapter<TQueueMessage, TMessageBatch> : IQueueDataAdapter<TQueueMessage>
    {
        /// <summary>
        /// Creates a batch container from a cloud queue message
        /// </summary>
        /// <param name="queueMessage">The queue message.</param>
        /// <param name="sequenceId">The sequence identifier.</param>
        /// <returns>The message batch.</returns>
        TMessageBatch FromQueueMessage(TQueueMessage queueMessage, long sequenceId);
    }
}
