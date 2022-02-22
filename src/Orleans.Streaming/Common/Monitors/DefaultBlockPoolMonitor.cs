using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Block pool monitor used as a default option in GeneratorStreamProvider and MemoryStreamProvider.
    /// </summary>
    public class DefaultBlockPoolMonitor : IBlockPoolMonitor
    {
        protected ITelemetryProducer TelemetryProducer;
        protected Dictionary<string, string> LogProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultBlockPoolMonitor"/> class.
        /// </summary>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultBlockPoolMonitor(ITelemetryProducer telemetryProducer)
        {
            this.TelemetryProducer = telemetryProducer;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultBlockPoolMonitor"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions.</param>
        /// <param name="telemetryProducer">The telemetry producer.</param>
        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, ITelemetryProducer telemetryProducer)
            : this(telemetryProducer)
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
