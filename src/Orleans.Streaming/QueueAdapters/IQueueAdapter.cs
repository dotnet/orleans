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
        /// Gets the name of the adapter. Primarily for logging purposes
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T">The queue element type.</typeparam>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="events">The events.</param>
        /// <param name="token">The token.</param>
        /// <param name="requestContext">The request context.</param>
        /// <returns>Task.</returns>
        Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext);

        /// <summary>
        /// Creates a queue receiver for the specified queueId
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>The receiver.</returns>
        IQueueAdapterReceiver CreateReceiver(QueueId queueId);

        /// <summary>
        /// Gets a value indicating whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary>
        /// Gets the direction of this queue adapter: <see cref="StreamProviderDirection.ReadOnly"/>, <see cref="StreamProviderDirection.WriteOnly"/>, or <see cref="StreamProviderDirection.ReadWrite"/>.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        StreamProviderDirection Direction { get; }
    }

    /// <summary>
    /// Extension methods for <see cref="IQueueAdapter"/>
    /// </summary>
    public static class QueueAdapterExtensions
    {
        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided <paramref name="streamId"/>.
        /// </summary>
        /// <typeparam name="T">The queue element type.</typeparam>
        /// <param name="adapter">The adapter.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="evt">The event.</param>
        /// <param name="token">The token.</param>
        /// <param name="requestContext">The request context.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        public static Task QueueMessageAsync<T>(this IQueueAdapter adapter, StreamId streamId, T evt, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            return adapter.QueueMessageBatchAsync(streamId, new[] { evt }, token, requestContext);
        }
    }
}
