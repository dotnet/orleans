
using System;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Responsible for monitoring receiver performance metrics.
    /// </summary>
    public interface IEventHubReceiverMonitor
    {
        /// <summary>
        /// Track attempts to initialize the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        void TrackInitialization(bool success);

        /// <summary>
        /// Track attempts to read from the partition.    Tracked per partition read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        void TrackRead(bool success);
        /// <summary>
        /// Tracks messages read and time taken per successful read.  Tracked per successful partition read operation.
        /// </summary>
        /// <param name="count">Messages read.</param>
        /// <param name="callTime">Time spent in read operation.</param>
        void TrackMessagesRecieved(long count, TimeSpan callTime);
        /// <summary>
        /// Tracks the age of messages as they are read.  Tracked per successful partition read operation.
        /// NOTE: These metrics do not account for clock skew between host and EventHub ingestion service.
        /// </summary>
        /// <param name="oldest">The difference between now utc on host and the eventhub enqueue time of the oldest message in a set of messages read.</param>
        /// <param name="newest">The difference between now utc on host and the eventhub enqueue time of the newest message in a set of messages read.</param>
        void TrackAgeOfMessagesRead(TimeSpan oldest, TimeSpan newest);

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        void TrackShutdown(bool success);
    }
}
