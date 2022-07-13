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
        protected DefaultBlockPoolMonitor(KeyValuePair<string, object>[] dimensions)
        {
            _dimensions = dimensions;
            _totalMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_TOTAL_MEMORY, GetTotalMemory, unit: "bytes");
            _availableMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_AVAILABLE_MEMORY, GetAvailableMemory, unit: "bytes");
            _claimedMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_CLAIMED_MEMORY, GetClaimedMemory, unit: "bytes");
            _releasedMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_RELEASED_MEMORY, GetReleasedMemory, unit: "bytes");
            _allocatedMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.STREAMS_BLOCK_POOL_ALLOCATED_MEMORY, GetAllocatedMemory, unit: "bytes");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultBlockPoolMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions) : this(new KeyValuePair<string, object>[] { new ("BlockPoolId", dimensions.BlockPoolId) })
        {
        }

        private Measurement<long> GetTotalMemory() => new(_totalMemory, _dimensions);
        private Measurement<long> GetAvailableMemory() => new(_availableMemory, _dimensions);
        private Measurement<long> GetClaimedMemory() => new(_claimedMemory, _dimensions);
        private Measurement<long> GetReleasedMemory() => new(_releasedMemory, _dimensions);
        private Measurement<long> GetAllocatedMemory() => new(_allocatedMemory, _dimensions);

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
