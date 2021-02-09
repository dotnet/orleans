using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// block pool monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultBlockPoolMonitor : IBlockPoolMonitor
    {
        protected ITelemetryProducer TelemetryProducer;
        protected Dictionary<string, string> LogProperties;

        public DefaultBlockPoolMonitor(ITelemetryProducer telemetryProducer)
        {
            this.TelemetryProducer = telemetryProducer;
        }

        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            :this(telemetryProducer)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"BlockPoolId", dimensions.BlockPoolId},
            };
        }
        /// <inheritdoc />
        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            this.TelemetryProducer.TrackMetric("TotalMemoryInByte", totalMemoryInByte, this.LogProperties);
            this.TelemetryProducer.TrackMetric("AvailableMemoryInByte", availableMemoryInByte, this.LogProperties);
            this.TelemetryProducer.TrackMetric("ClaimedMemoryInByte", claimedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            this.TelemetryProducer.TrackMetric("ReleasedMemoryInByte", releasedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc />
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            this.TelemetryProducer.TrackMetric("AllocatedMemoryInByte", allocatedMemoryInByte, this.LogProperties);
        }
    }
}
