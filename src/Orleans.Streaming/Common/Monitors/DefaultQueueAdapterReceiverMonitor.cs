using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Queue adapter receiver monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultQueueAdapterReceiverMonitor : IQueueAdapterReceiverMonitor
    {
        private readonly Counter<long> _initializationFailureCounter;
        private readonly Counter<long> _initializationCallTimeCounter;
        private readonly Counter<long> _initializationExceptionCounter;
        private readonly Counter<long> _readFailureCounter;
        private readonly Counter<long> _readCallTimeCounter;
        private readonly Counter<long> _readExceptionCounter;
        private readonly Counter<long> _shutdownFailureCounter;
        private readonly Counter<long> _shutdownCallTimeCounter;
        private readonly Counter<long> _shutdownExceptionCounter;
        private readonly ObservableCounter<long> _messagesReceivedCounter;
        private readonly ObservableGauge<long> _oldestMessageReadEnqueueTimeToNowCounter;
        private readonly ObservableGauge<long> _newestMessageReadEnqueueTimeToNowCounter;
        private readonly KeyValuePair<string, object>[] _dimensions;
        private ValueStopwatch _oldestMessageReadEnqueueAge;
        private ValueStopwatch _newestMessageReadEnqueueAge;
        private long _messagesReceived;

        protected DefaultQueueAdapterReceiverMonitor(KeyValuePair<string,object>[] dimensions)
        {
            _dimensions = dimensions;
            _initializationFailureCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_INITIALIZATION_FAILURES);
            _initializationCallTimeCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_INITIALIZATION_DURATION);
            _initializationExceptionCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_INITIALIZATION_EXCEPTIONS);

            _readFailureCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_READ_FAILURES);
            _readCallTimeCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_READ_DURATION);
            _readExceptionCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_READ_EXCEPTIONS);

            _shutdownFailureCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_SHUTDOWN_FAILURES);
            _shutdownCallTimeCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_SHUTDOWN_DURATION);
            _shutdownExceptionCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.STREAMS_QUEUE_SHUTDOWN_EXCEPTIONS);

            _messagesReceivedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_MESSAGES_RECEIVED, GetMessagesReceivedCount);
            _oldestMessageReadEnqueueTimeToNowCounter = Instruments.Meter.CreateObservableGauge<long>(InstrumentNames.STREAMS_QUEUE_OLDEST_MESSAGE_ENQUEUE_AGE, GetOldestMessageReadEnqueueAge);
            _newestMessageReadEnqueueTimeToNowCounter = Instruments.Meter.CreateObservableGauge<long>(InstrumentNames.STREAMS_QUEUE_NEWEST_MESSAGE_ENQUEUE_AGE, GetNewestMessageReadEnqueueAge);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultQueueAdapterReceiverMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        public DefaultQueueAdapterReceiverMonitor(ReceiverMonitorDimensions dimensions) : this(new KeyValuePair<string,object>[] { new("QueueId", dimensions.QueueId) })
        {
        }

        private Measurement<long> GetOldestMessageReadEnqueueAge() => new(_oldestMessageReadEnqueueAge.ElapsedTicks, _dimensions);
        private Measurement<long> GetNewestMessageReadEnqueueAge() => new(_newestMessageReadEnqueueAge.ElapsedTicks, _dimensions);
        private Measurement<long> GetMessagesReceivedCount() => new(_messagesReceived, _dimensions);

        /// <inheritdoc />
        public void TrackInitialization(bool success, TimeSpan callTime, Exception exception)
        {
            _initializationFailureCounter.Add(success ? 0 : 1, _dimensions);
            _initializationCallTimeCounter.Add(callTime.Ticks, _dimensions);
            _initializationExceptionCounter.Add(exception is null ? 0 : 1, _dimensions);
        }

        /// <inheritdoc />
        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            _readFailureCounter.Add(success ? 0 : 1, _dimensions);
            _readCallTimeCounter.Add(callTime.Ticks, _dimensions);
            _readExceptionCounter.Add(exception is null ? 0 : 1, _dimensions);
        }

        /// <inheritdoc />
        public void TrackMessagesReceived(long count, DateTime? oldestMessageEnqueueTimeUtc, DateTime? newestMessageEnqueueTimeUtc)
        {
            var now = DateTime.UtcNow;
            Interlocked.Add(ref _messagesReceived, count);
            if (oldestMessageEnqueueTimeUtc.HasValue)
            {
                var delta = now - oldestMessageEnqueueTimeUtc.Value;
                _oldestMessageReadEnqueueAge = ValueStopwatch.StartNew(delta);
            }

            if (newestMessageEnqueueTimeUtc.HasValue)
            {
                var delta = now - newestMessageEnqueueTimeUtc.Value;
                _newestMessageReadEnqueueAge = ValueStopwatch.StartNew(delta);
            }
        }

        /// <inheritdoc />
        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            _shutdownFailureCounter.Add(success ? 0 : 1, _dimensions);
            _shutdownCallTimeCounter.Add(callTime.Ticks, _dimensions);
            _shutdownExceptionCounter.Add(exception is null ? 0 : 1, _dimensions);
        }
    }
}
