using Orleans.Runtime;
using System;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Queue adapter receiver monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultQueueAdapterReceiverMonitor : IQueueAdapterReceiverMonitor
    {
        protected readonly ITelemetryProducer TelemetryProducer;
        protected Dictionary<string, string> LogProperties;

        public DefaultQueueAdapterReceiverMonitor(ITelemetryProducer telemetryProducer)
        {
            this.TelemetryProducer = telemetryProducer;
        }

        public DefaultQueueAdapterReceiverMonitor(ReceiverMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            :this(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"QueueId", dimensions.QueueId},
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
            this.TelemetryProducer.TrackMetric("InitializationFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("InitializationCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("InitializationException", exception == null ? 0 : 1, this.LogProperties);
        }

        /// <summary>
        /// Track attempts to read from the queue.    Tracked per queue read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            this.TelemetryProducer.TrackMetric("ReadFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ReadCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ReadException", exception == null ? 0 : 1, this.LogProperties);
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
            this.TelemetryProducer.TrackMetric("MessagesReceived", count, this.LogProperties);
            if (oldestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("OldestMessageReadEnqueueTimeToNow", now - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);
            if (newestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("NewestMessageReadEnqueueTimeToNow", now - newestMessageEnqueueTimeUtc.Value, this.LogProperties);
        }

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            this.TelemetryProducer.TrackMetric("ShutdownFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ShutdownCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ShutdownException", exception == null ? 0 : 1, this.LogProperties);
        }
    }
}
