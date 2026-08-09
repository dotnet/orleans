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
        /// Retrieves batches from a message queue.
        /// </summary>
        /// <param name="maxCount">
        /// The maximum number of message batches to retrieve.
        /// </param>
        /// <returns>The message batches.</returns>
        Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount);

        /// <summary>
        /// Retrieves batches from a message queue.
        /// </summary>
        /// <param name="maxCount">The maximum number of message batches to retrieve.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The message batches.</returns>
        Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetQueueMessagesAsync(maxCount).WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Notifies the adapter receiver that the messages were delivered to all consumers,
        /// so the receiver can take an appropriate action (e.g., delete the messages from a message queue).
        /// </summary>
        /// <param name="messages">
        /// The message batches.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task MessagesDeliveredAsync(IList<IBatchContainer> messages);

        /// <summary>
        /// Notifies the adapter receiver that the messages were delivered to all consumers.
        /// </summary>
        /// <param name="messages">The message batches.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MessagesDeliveredAsync(messages).WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Receiver is no longer used. Shutdown and clean up.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Shutdown(TimeSpan timeout);
    }
}
