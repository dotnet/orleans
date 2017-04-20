
using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Default EventHub receiver monitor that tracks metrics using loggers PKI support.
    /// </summary>
    public class DefaultEventHubReceiverMonitor : IEventHubReceiverMonitor
    {
        private readonly Logger logger;
        private readonly Dictionary<string, string> LogProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="hubPath">EventHub path.  Hub Name</param>
        /// <param name="hubPartition">EventHub partition</param>
        /// <param name="logger"></param>
        public DefaultEventHubReceiverMonitor(string hubPath, string hubPartition, Logger logger)
        {
            this.logger = logger;
            LogProperties = new Dictionary<string, string>
            {
                {"Path", hubPath},
                {"Partition", hubPartition}
            };
        }

        /// <summary>
        /// Track attempts to initialize the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        public void TrackInitialization(bool success)
        {
            logger.TrackMetric("Initialization", success ? 0 : 1, LogProperties);
        }

        /// <summary>
        /// Track attempts to read from the partition.    Tracked per partition read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        public void TrackRead(bool success)
        {
            logger.TrackMetric("ReadFailure", success ? 0 : 1, LogProperties);
        }

        /// <summary>
        /// Tracks messages read and time taken per successful read.  Tracked per successful partition read operation.
        /// </summary>
        /// <param name="count">Messages read.</param>
        /// <param name="callTime">Time spent in read operation.</param>
        public void TrackMessagesRecieved(long count, TimeSpan callTime)
        {
            logger.TrackMetric("MessagesRecieved", count, LogProperties);
            logger.TrackMetric("ReceiveTime", callTime, LogProperties);
        }

        /// <summary>
        /// Tracks the age of messages as they are read.  Tracked per successful partition read operation.
        /// NOTE: These metrics do not account for clock skew between host and EventHub ingestion service.
        /// </summary>
        /// <param name="oldest">The difference between now utc on host and the eventhub enqueue time of the oldest message in a set of messages read.</param>
        /// <param name="newest">The difference between now utc on host and the eventhub enqueue time of the newest message in a set of messages read.</param>
        public void TrackAgeOfMessagesRead(TimeSpan newest, TimeSpan oldest)
        {
            logger.TrackMetric("AgeOfMessagesBeingProcessed", newest, LogProperties);
        }

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        public void TrackShutdown(bool success)
        {
            logger.TrackMetric("Shutdown", success ? 0 : 1, LogProperties);
        }
    }

}
