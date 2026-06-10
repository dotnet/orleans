using Orleans.Runtime;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

#nullable disable
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

        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, OrleansInstruments instruments)
            : this(new KeyValuePair<string, object>[] { new("BlockPoolId", dimensions.BlockPoolId) }, instruments.Meter)
        {
        }

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
