using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Queue adapter receiver monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultQueueAdapterReceiverMonitor : IQueueAdapterReceiverMonitor
    {
        protected Logger Logger;
        protected Dictionary<string, string> LogProperties;

        public DefaultQueueAdapterReceiverMonitor(Logger logger)
        {
            this.Logger = logger;
        }

        public DefaultQueueAdapterReceiverMonitor(ReceiverMonitorDimensions dimensions, Logger logger)
            :this(logger)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"QueueId", dimensions.QueueId},
                {"HostName", dimensions.NodeConfig.HostNameOrIPAddress }
            };
        }
        /// <summary>
        /// Track attempts to initialize the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackInitialization(bool success, TimeSpan callTime, Exception exception)
        {
            this.Logger.TrackMetric("InitializationFailure", success ? 0 : 1, this.LogProperties);
            this.Logger.TrackMetric("InitializationCallTime", callTime, this.LogProperties);
            this.Logger.TrackMetric("InitializationException", exception == null ? 0 : 1, this.LogProperties);
        }

        /// <summary>
        /// Track attempts to read from the queue.    Tracked per queue read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            this.Logger.TrackMetric("ReadFailure", success ? 0 : 1, this.LogProperties);
            this.Logger.TrackMetric("ReadCallTime", callTime, this.LogProperties);
            this.Logger.TrackMetric("ReadException", exception == null ? 0 : 1, this.LogProperties);
        }

        /// <summary>
        /// Tracks messages read and time taken per successful read.  Tracked per successful queue read operation.
        /// </summary>
        /// <param name="count">Messages read.</param>
        /// <param name="oldestMessageEnqueueTimeUtc"></param>
        /// <param name="newestMessageEnqueueTimeUtc"></param>
        public void TrackMessagesReceived(long count, DateTime? oldestMessageEnqueueTimeUtc, DateTime? newestMessageEnqueueTimeUtc)
        {
            var now = DateTime.UtcNow;
            this.Logger.TrackMetric("MessagesRecieved", count, this.LogProperties);
            if (oldestMessageEnqueueTimeUtc.HasValue)
                this.Logger.TrackMetric("OldestMessageReadEnqueueTimeToNow", now - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);
            if (newestMessageEnqueueTimeUtc.HasValue)
                this.Logger.TrackMetric("NewestMessageReadEnqueueTimeToNow", now - newestMessageEnqueueTimeUtc.Value, this.LogProperties);
        }

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            this.Logger.TrackMetric("ShutdownFailure", success ? 0 : 1, this.LogProperties);
            this.Logger.TrackMetric("ShutdownCallTime", callTime, this.LogProperties);
            this.Logger.TrackMetric("ShutdownException", exception == null ? 0 : 1, this.LogProperties);
        }
    }
}
