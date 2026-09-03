using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.  
    /// </summary>
    public interface IQueueAdapterReceiver
    {
        /// <summary>
        /// Initializes this receiver.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Initialize(TimeSpan timeout);

        /// <summary>
        /// Initializes this receiver.
        /// </summary>
        /// <param name="timeout">The timeout for this operation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Initialize(TimeSpan timeout, CancellationToken cancellationToken) => Initialize(timeout);

        /// <summary>
        /// Retrieves batches from a message queue.
        /// </summary>
        /// <param name="maxCount">
        /// The maximum number of message batches to retrieve.
        /// </param>
        /// <returns>The message batches.</returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount);

        /// <summary>
        /// Retrieves batches from a message queue.
        /// </summary>
        /// <param name="maxCount">The maximum number of message batches to retrieve.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The message batches.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount, CancellationToken cancellationToken)
            => GetQueueMessagesAsync(maxCount);
#pragma warning restore CS0618

        /// <summary>
        /// Notifies the adapter receiver that the messages were delivered to all consumers,
        /// so the receiver can take an appropriate action (e.g., delete the messages from a message queue).
        /// </summary>
        /// <param name="messages">
        /// The message batches.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task MessagesDeliveredAsync(IList<IBatchContainer> messages);

        /// <summary>
        /// Notifies the adapter receiver that the messages were delivered to all consumers.
        /// </summary>
        /// <param name="messages">The message batches.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken)
            => MessagesDeliveredAsync(messages);
#pragma warning restore CS0618

        /// <summary>
        /// Receiver is no longer used. Shutdown and clean up.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Shutdown(TimeSpan timeout);

        /// <summary>
        /// Shuts down this receiver and cleans up.
        /// </summary>
        /// <param name="timeout">The timeout for this operation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Shutdown(TimeSpan timeout, CancellationToken cancellationToken) => Shutdown(timeout);
    }
}
