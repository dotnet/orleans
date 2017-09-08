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
        protected IMetricsWriter MetricsWriter;
        protected Dictionary<string, string> LogProperties;

        public DefaultBlockPoolMonitor(IMetricsWriter metricsWriter)
        {
            this.MetricsWriter = metricsWriter;
        }

        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, IMetricsWriter metricsWriter)
            :this(metricsWriter)
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
            this.MetricsWriter.TrackMetric("TotalMemoryInByte", totalMemoryInByte, this.LogProperties);
            this.MetricsWriter.TrackMetric("AvailableMemoryInByte", availableMemoryInByte, this.LogProperties);
            this.MetricsWriter.TrackMetric("ClaimedMemoryInByte", claimedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            this.MetricsWriter.TrackMetric("ReleasedMemoryInByte", releasedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            this.MetricsWriter.TrackMetric("AllocatedMemoryInByte", allocatedMemoryInByte, this.LogProperties);
        }
    }
}
