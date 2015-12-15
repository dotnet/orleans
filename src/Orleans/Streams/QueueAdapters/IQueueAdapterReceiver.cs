using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.  
    /// </summary>
    public interface IQueueAdapterReceiver
    {
        /// <summary>
        /// Initialize this receiver.
        /// </summary>
        /// <returns></returns>
        Task Initialize(TimeSpan timeout);

        /// <summary>
        /// Retrieves batches from a message queue.
        /// </summary>
        /// <returns></returns>
        Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount);

        /// <summary>
        /// Notifies the adapter receiver that the mesages were delivered to all consumers,
        /// so the receiver can take an appropriate action (e.g., delete the messages from a message queue).
        /// </summary>
        /// <returns></returns>
        Task MessagesDeliveredAsync(IList<IBatchContainer> messages);

        /// <summary>
        /// Receiver is no longer used.  Shutdown and clean up.
        /// </summary>
        /// <returns></returns>
        Task Shutdown(TimeSpan timeout);
    }
}
