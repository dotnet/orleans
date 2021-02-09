using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream queue storage adapter.  This is an abstraction layer that hides the implementation details of the underlying queuing system.
    /// </summary>
    public interface IQueueAdapter
    {
        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streamId"></param>
        /// <param name="events"></param>
        /// <param name="token"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext);

        /// <summary>
        /// Creates a queue receiver for the specified queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        IQueueAdapterReceiver CreateReceiver(QueueId queueId);

        /// <summary>
        /// Determines whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary>
        /// Direction of this queue adapter: Read, Write or ReadWrite.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        StreamProviderDirection Direction { get; }
    }

    public static class QueueAdapterExtensions
    {
        /// <summary>Writes a set of events to the queue as a single batch associated with the provided streamId.</summary>
        public static Task QueueMessageAsync<T>(this IQueueAdapter adapter, StreamId streamId, T evt, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            return adapter.QueueMessageBatchAsync(streamId, new[] { evt }, token, requestContext);
        }
    }
}
