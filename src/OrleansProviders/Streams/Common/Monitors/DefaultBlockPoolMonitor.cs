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
        protected Logger Logger;
        protected Dictionary<string, string> LogProperties;

        public DefaultBlockPoolMonitor(Logger logger)
        {
            this.Logger = logger;
        }

        public DefaultBlockPoolMonitor(BlockPoolMonitorDimensions dimensions, Logger logger)
            :this(logger)
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
            this.Logger.TrackMetric("TotalMemoryInByte", totalMemoryInByte, this.LogProperties);
            this.Logger.TrackMetric("AvailableMemoryInByte", availableMemoryInByte, this.LogProperties);
            this.Logger.TrackMetric("ClaimedMemoryInByte", claimedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryReleased(long releasedMemoryInByte)
        {
            this.Logger.TrackMetric("ReleasedMemoryInByte", releasedMemoryInByte, this.LogProperties);
        }

        /// <inheritdoc cref="IBlockPoolMonitor"/>
        public void TrackMemoryAllocated(long allocatedMemoryInByte)
        {
            this.Logger.TrackMetric("AllocatedMemoryInByte", allocatedMemoryInByte, this.LogProperties);
        }
    }
}
