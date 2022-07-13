using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// cache monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultCacheMonitor : ICacheMonitor
    {
        private readonly KeyValuePair<string, object>[] _dimensions;
        private readonly ObservableCounter<long> _queueCacheSizeCounter;
        private readonly ObservableCounter<long> _queueCacheMessagesAddedCounter;
        private readonly ObservableCounter<long> _queueCacheMessagesPurgedCounter;
        private readonly ObservableCounter<long> _queueCacheMemoryAllocatedCounter;
        private readonly ObservableCounter<long> _queueCacheMemoryReleasedCounter;
        private readonly ObservableGauge<long> _oldestMessageReadEnqueueTimeToNowCounter;
        private readonly ObservableGauge<long> _newestMessageReadEnqueueTimeToNowCounter;
        private readonly ObservableGauge<double> _currentPressureCounter;
        private readonly ObservableGauge<int> _underPressureCounter;
        private readonly ObservableGauge<double> _pressureContributionCounter;
        private readonly ObservableCounter<long> _queueCacheMessageCountCounter;
        private readonly ConcurrentDictionary<string, PressureMonitorStatistics> _pressureMonitors = new();
        private long _messagesPurged;
        private long _messagesAdded;
        private long _memoryReleased;
        private long _memoryAllocated;
        private long _totalCacheSize;
        private long _messageCount;
        private ValueStopwatch _oldestMessageDequeueAgo;
        private ValueStopwatch _oldestToNewestAge;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultCacheMonitor"/> class.
        /// </summary>
        protected DefaultCacheMonitor(KeyValuePair<string, object>[] dimensions)
        {
            _dimensions = dimensions;
            _queueCacheSizeCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_SIZE, () => new(_totalCacheSize, _dimensions), unit: "bytes");
            _queueCacheMessageCountCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_LENGTH, () => new(_messageCount, _dimensions), unit: "messages");
            _queueCacheMessagesAddedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_MESSAGES_ADDED, () => new(_messagesAdded, _dimensions));
            _queueCacheMessagesPurgedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_MESSAGES_PURGED, () => new(_messagesPurged, _dimensions));
            _queueCacheMemoryAllocatedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_MEMORY_ALLOCATED, () => new(_memoryAllocated, _dimensions));
            _queueCacheMemoryReleasedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_QUEUE_CACHE_MEMORY_RELEASED, () => new(_memoryReleased, _dimensions));
            _oldestMessageReadEnqueueTimeToNowCounter = Instruments.Meter.CreateObservableGauge<long>(InstrumentNames.STREAMS_QUEUE_CACHE_OLDEST_TO_NEWEST_DURATION, GetOldestToNewestAge);
            _newestMessageReadEnqueueTimeToNowCounter = Instruments.Meter.CreateObservableGauge<long>(InstrumentNames.STREAMS_QUEUE_CACHE_OLDEST_AGE, GetOldestAge);
            _currentPressureCounter = Instruments.Meter.CreateObservableGauge<double>(InstrumentNames.STREAMS_QUEUE_CACHE_PRESSURE, () => GetPressureMonitorMeasurement(monitor => monitor.CurrentPressure));
            _underPressureCounter = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.STREAMS_QUEUE_CACHE_UNDER_PRESSURE, () => GetPressureMonitorMeasurement(monitor => monitor.UnderPressure));
            _pressureContributionCounter = Instruments.Meter.CreateObservableGauge<double>(InstrumentNames.STREAMS_QUEUE_CACHE_PRESSURE_CONTRIBUTION_COUNT, () => GetPressureMonitorMeasurement(monitor => monitor.PressureContributionCount));
            IEnumerable<Measurement<T>> GetPressureMonitorMeasurement<T>(Func<PressureMonitorStatistics, T> selector) where T : struct
            {
                foreach (var monitor in _pressureMonitors)
                {
                    yield return new Measurement<T>(selector(monitor.Value), monitor.Value.Dimensions);
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultCacheMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        public DefaultCacheMonitor(CacheMonitorDimensions dimensions) : this(new KeyValuePair<string, object>[] { new("QueueId", dimensions.QueueId) })
        {
        }

        private Measurement<long> GetOldestToNewestAge() => new(_oldestToNewestAge.ElapsedTicks, _dimensions);
        private Measurement<long> GetOldestAge() => new(_oldestMessageDequeueAgo.ElapsedTicks, _dimensions);

        /// <inheritdoc />
        public void TrackCachePressureMonitorStatusChange(
            string pressureMonitorType,
            bool underPressure,
            double? cachePressureContributionCount,
            double? currentPressure,
            double? flowControlThreshold)
        {
            var monitor = _pressureMonitors.GetOrAdd(pressureMonitorType, static (key, dimensions) => new PressureMonitorStatistics(key, dimensions), _dimensions);
            monitor.UnderPressure = underPressure ? 1 : 0;
            if (cachePressureContributionCount.HasValue)
            {
                monitor.PressureContributionCount = cachePressureContributionCount.Value;
            }

            if (currentPressure.HasValue)
            {
                monitor.CurrentPressure = currentPressure.Value;
            }

        }

        /// <inheritdoc />
        public void ReportCacheSize(long totalCacheSizeInByte) => _totalCacheSize = totalCacheSizeInByte;

        /// <inheritdoc />
        public void ReportMessageStatistics(DateTime? oldestMessageEnqueueTimeUtc, DateTime? oldestMessageDequeueTimeUtc, DateTime? newestMessageEnqueueTimeUtc, long totalMessageCount)
        {
            if (oldestMessageEnqueueTimeUtc.HasValue && newestMessageEnqueueTimeUtc.HasValue)
            {
                _oldestToNewestAge = ValueStopwatch.StartNew(newestMessageEnqueueTimeUtc.Value - oldestMessageEnqueueTimeUtc.Value);
            }

            if (oldestMessageDequeueTimeUtc.HasValue)
            {
                _oldestMessageDequeueAgo = ValueStopwatch.StartNew(DateTime.UtcNow - oldestMessageDequeueTimeUtc.Value);
            }

            _messageCount = totalMessageCount;
        }

        /// <inheritdoc />
        public void TrackMemoryAllocated(int memoryInByte) => Interlocked.Add(ref _memoryAllocated, memoryInByte);

        /// <inheritdoc />
        public void TrackMemoryReleased(int memoryInByte) => Interlocked.Add(ref _memoryReleased, memoryInByte);

        /// <inheritdoc />
        public void TrackMessagesAdded(long messageAdded) => Interlocked.Add(ref _messagesAdded, messageAdded);

        /// <inheritdoc />
        public void TrackMessagesPurged(long messagePurged) => Interlocked.Add(ref _messagesPurged, messagePurged);

        private sealed class PressureMonitorStatistics
        {
            public PressureMonitorStatistics(string type, KeyValuePair<string, object>[] dimensions)
            {
                Dimensions = new KeyValuePair<string, object>[dimensions.Length + 1];
                dimensions.CopyTo(Dimensions, 0);
                Dimensions[^1] = new KeyValuePair<string, object>("PressureMonitorType", type);
            }

            public KeyValuePair<string, object>[] Dimensions { get; }
            public double PressureContributionCount { get; set; }
            public double CurrentPressure { get; set; }
            public int UnderPressure { get; set; }
        }
    }
}
