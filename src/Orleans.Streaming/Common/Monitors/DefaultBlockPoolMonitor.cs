using Orleans.Runtime;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Block pool monitor used as a default option in GeneratorStreamProvider and MemoryStreamProvider.
    /// </summary>
    public class DefaultBlockPoolMonitor : IBlockPoolMonitor
    {
        /// <summary>
        /// The dimensions attached to metrics reported by this monitor.
        /// </summary>
        protected KeyValuePair<string, object>[] _dimensions;
        private readonly ObservableCounter<long> _totalMemoryCounter;
        private readonly ObservableCounter<long> _availableMemoryCounter;
        private readonly ObservableCounter<long> _claimedMemoryCounter;
        private readonly ObservableCounter<long> _releasedMemoryCounter;
        private readonly ObservableCounter<long> _allocatedMemoryCounter;
        private long _totalMemory;
        private long _availableMemory;
        private long _claimedMemory;
        private long _releasedMemory;
        private long _allocatedMemory;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultBlockPoolMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The block pool metric dimensions.</param>
        /// <param name="instruments">The Orleans runtime instruments.</param>
        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, OrleansInstruments instruments)
            : this(new KeyValuePair<string, object>[] { new("BlockPoolId", dimensions.BlockPoolId) }, instruments.Meter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultBlockPoolMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions attached to metrics reported by this monitor.</param>
        /// <param name="instruments">The Orleans runtime instruments.</param>
        protected DefaultBlockPoolMonitor(KeyValuePair<string, object>[] dimensions, OrleansInstruments instruments)
            : this(dimensions, instruments.Meter)
        {
        }

        private DefaultBlockPoolMonitor(KeyValuePair<string, object>[] dimensions, Meter meter)
        {
            _dimensions = dimensions;
            _totalMemoryCounter = meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_TOTAL_MEMORY, GetTotalMemory, unit: "bytes");
            _availableMemoryCounter = meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_AVAILABLE_MEMORY, GetAvailableMemory, unit: "bytes");
            _claimedMemoryCounter = meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_CLAIMED_MEMORY, GetClaimedMemory, unit: "bytes");
            _releasedMemoryCounter = meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_RELEASED_MEMORY, GetReleasedMemory, unit: "bytes");
            _allocatedMemoryCounter = meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_ALLOCATED_MEMORY, GetAllocatedMemory, unit: "bytes");
        }

        // The tag values in _dimensions are never actually null; the cast only widens the nullability annotation of the array
        // element type to satisfy the (nullable-annotated) System.Diagnostics.Metrics.Measurement<T> constructor below, which is
        // safe since the underlying CLR array type is identical regardless of the element's nullable annotation.
        private Measurement<long> GetTotalMemory() => new(_totalMemory, (KeyValuePair<string, object?>[])(object)_dimensions);
        private Measurement<long> GetAvailableMemory() => new(_availableMemory, (KeyValuePair<string, object?>[])(object)_dimensions);
        private Measurement<long> GetClaimedMemory() => new(_claimedMemory, (KeyValuePair<string, object?>[])(object)_dimensions);
        private Measurement<long> GetReleasedMemory() => new(_releasedMemory, (KeyValuePair<string, object?>[])(object)_dimensions);
        private Measurement<long> GetAllocatedMemory() => new(_allocatedMemory, (KeyValuePair<string, object?>[])(object)_dimensions);

        /// <inheritdoc />
        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            _totalMemory = totalMemoryInByte;
            _availableMemory = availableMemoryInByte;
            _claimedMemory = claimedMemoryInByte;
        }

        /// <inheritdoc />
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            Interlocked.Add(ref _releasedMemory, releasedMemoryInByte);
        }

        /// <inheritdoc />
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            Interlocked.Add(ref _allocatedMemory, allocatedMemoryInByte);
        }
    }
}
