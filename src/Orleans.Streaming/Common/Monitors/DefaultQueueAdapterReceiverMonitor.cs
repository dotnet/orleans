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

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultQueueAdapterReceiverMonitor"/> class.
        /// </summary>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultQueueAdapterReceiverMonitor(ITelemetryProducer telemetryProducer)
        {
            this.TelemetryProducer = telemetryProducer;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultQueueAdapterReceiverMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultQueueAdapterReceiverMonitor(ReceiverMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            :this(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"QueueId", dimensions.QueueId},
            };
        }

        /// <inheritdoc />
        public void TrackInitialization(bool success, TimeSpan callTime, Exception exception)
        {
            this.TelemetryProducer.TrackMetric("InitializationFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("InitializationCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("InitializationException", exception == null ? 0 : 1, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            this.TelemetryProducer.TrackMetric("ReadFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ReadCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ReadException", exception == null ? 0 : 1, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMessagesReceived(long count, DateTime? oldestMessageEnqueueTimeUtc, DateTime? newestMessageEnqueueTimeUtc)
        {
            var now = DateTime.UtcNow;
            this.TelemetryProducer.TrackMetric("MessagesReceived", count, this.LogProperties);
            if (oldestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("OldestMessageReadEnqueueTimeToNow", now - oldestMessageEnqueueTimeUtc.Value, this.LogProperties);
            if (newestMessageEnqueueTimeUtc.HasValue)
                this.TelemetryProducer.TrackMetric("NewestMessageReadEnqueueTimeToNow", now - newestMessageEnqueueTimeUtc.Value, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            this.TelemetryProducer.TrackMetric("ShutdownFailure", success ? 0 : 1, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ShutdownCallTime", callTime, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ShutdownException", exception == null ? 0 : 1, this.LogProperties);
        }
    }
}
