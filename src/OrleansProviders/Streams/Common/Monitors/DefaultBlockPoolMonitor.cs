using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// block pool monitor used as a default option in GeneratorStreamprovider and MemoryStreamProvider
    /// </summary>
    public class DefaultBlockPoolMonitor : IBlockPoolMonitor
    {
        protected ITelemetryClient TelemetryClient;
        protected Dictionary<string, string> LogProperties;

        public DefaultBlockPoolMonitor(ITelemetryClient telemetryClient)
        {
            this.TelemetryClient = telemetryClient;
        }

        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, ITelemetryClient telemetryClient)
            :this(telemetryClient)
        {
            this.LogProperties = new Dictionary<string, string>
            {
                {"BlockPoolId", dimensions.BlockPoolId},
                {"HostName", dimensions.NodeConfig.HostNameOrIPAddress }
            };
        }
        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void Report(long totalMemoryInByte, long availableMemoryInByte, long claimedMemoryInByte)
        {
            this.TelemetryClient.TrackMetric("TotalMemoryInByte", totalMemoryInByte, this.LogProperties);
            this.TelemetryClient.TrackMetric("AvailableMemoryInByte", availableMemoryInByte, this.LogProperties);
            this.TelemetryClient.TrackMetric("ClaimedMemoryInByte", claimedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            this.TelemetryClient.TrackMetric("ReleasedMemoryInByte", releasedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            this.TelemetryClient.TrackMetric("AllocatedMemoryInByte", allocatedMemoryInByte, this.LogProperties);
        }
    }
}
